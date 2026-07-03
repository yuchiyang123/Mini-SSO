using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mini_SSO.Common.Enums;
using Mini_SSO.Common.Exceptions;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Model.Entities;

namespace Mini_SSO.Services
{
    public class AuthService(AuthContext context, IConfiguration configuration)
    {
        public readonly IConfiguration _configuration = configuration;

        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

        public async Task<bool> LoginAsync(string userName, string password, string ipAddress)
        {
            await EnsureNotLockedOutAsync(ipAddress);

            var user = await context.Users.FirstOrDefaultAsync(x => x.UserName == userName);
            var hasher = new PasswordHasher<Users>();
            var success =
                user?.PasswordHash is not null
                && hasher.VerifyHashedPassword(user, user.PasswordHash, password)
                    != PasswordVerificationResult.Failed;

            await RecordLoginAttemptAsync(ipAddress, success);

            if (!success)
            {
                throw new DomainValidationException("使用者帳號或是密碼錯誤");
            }

            return true;
        }

        private async Task EnsureNotLockedOutAsync(string ipAddress)
        {
            var attempt = await context.LoginAttempts.FirstOrDefaultAsync(a =>
                a.IpAddress == ipAddress
            );

            if (attempt?.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
            {
                var retryAfterSeconds = (int)
                    Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds);
                throw new TooManyRequestsException(
                    $"登入失敗次數過多，請於 {retryAfterSeconds} 秒後再試。",
                    retryAfterSeconds
                );
            }
        }

        private async Task RecordLoginAttemptAsync(string ipAddress, bool success)
        {
            var attempt = await context.LoginAttempts.FirstOrDefaultAsync(a =>
                a.IpAddress == ipAddress
            );

            if (attempt is null)
            {
                attempt = new LoginAttempt { IpAddress = ipAddress };
                context.LoginAttempts.Add(attempt);
            }

            attempt.LastAttemptAt = DateTime.UtcNow;

            if (success)
            {
                attempt.FailedCount = 0;
                attempt.LockedUntil = null;
            }
            else
            {
                attempt.FailedCount++;
                if (attempt.FailedCount >= MaxFailedAttempts)
                {
                    attempt.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
                    attempt.FailedCount = 0;
                }
            }

            await context.SaveChangesAsync();
        }

        public async Task<Guid> GetIdByUserName(string userName)
        {
            return await context
                .Users.Where(x => x.UserName == userName)
                .Select(x => x.UserId)
                .FirstOrDefaultAsync();
        }

        public async Task<bool> ValidUserName(string userName)
        {
            var isRepeat = await context.Users.AnyAsync(x => x.UserName == userName);
            return !isRepeat;
        }

        public async Task<CurrentUserDto> GetCurrentUserAsync(Guid userId)
        {
            var user =
                await context
                    .Users.Include(u => u.UserLogins)
                    .FirstOrDefaultAsync(u => u.UserId == userId)
                ?? throw new DomainNotFoundException(nameof(Users), userId);

            return new CurrentUserDto
            {
                UserId = user.UserId,
                UserName = user.UserName,
                Email = user.Email,
                HasPassword = user.PasswordHash is not null,
                LinkedProviders = user.UserLogins.Select(l => l.Provider.ToString()).ToList(),
            };
        }

        public string GenerateeToken(string userId)
        {
            var jwtConfig = _configuration.GetSection("Jwt");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"]!));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            var token = new JwtSecurityToken(
                issuer: jwtConfig["Issuer"],
                audience: jwtConfig["Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(double.Parse(jwtConfig["ExpireMinutes"]!)),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task CreateUserAsync(CreateUserDto userDto)
        {
            var entity = new Users { UserName = userDto.UserName, Email = userDto.Email };
            var hasher = new PasswordHasher<Users>();
            entity.PasswordHash = hasher.HashPassword(entity, userDto.Password);
            context.Users.Add(entity);
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
                when (ex.InnerException is SqlException { Number: 2601 or 2627 }) // 違反唯一性
            {
                throw new DomainValidationException(
                    new Dictionary<string, string[]>
                    {
                        [nameof(userDto.UserName)] = ["Username already exists."],
                    }
                );
            }
        }

        /// <summary>
        /// 依外部登入資訊找出（或建立）對應的本地使用者，並回傳其 UserId。
        /// 已綁定過 -> 直接回傳；Email 已存在 -> 綁定到既有帳號；否則建立新帳號（無密碼）。
        /// </summary>
        public async Task<Guid> ExternalLoginAsync(
            ProviderEnums provider,
            string providerKey,
            string email,
            string? displayName
        )
        {
            var existingLogin = await context.UserLogins.FirstOrDefaultAsync(l =>
                l.Provider == provider && l.ProviderKey == providerKey
            );

            if (existingLogin is not null)
            {
                return existingLogin.UserId;
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user is null)
            {
                user = new Users
                {
                    UserName = await GenerateUniqueUserNameAsync(email, displayName),
                    Email = email,
                    PasswordHash = null,
                };
                context.Users.Add(user);
            }

            context.UserLogins.Add(
                new UserLogin
                {
                    Provider = provider,
                    ProviderKey = providerKey,
                    User = user,
                }
            );

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
                when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new DomainValidationException(
                    "This external account could not be linked due to a conflicting record; please try again."
                );
            }

            return user.UserId;
        }

        private async Task<string> GenerateUniqueUserNameAsync(string email, string? displayName)
        {
            var baseSource = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : email.Split('@')[0];
            var baseName = new string(baseSource.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(baseName))
            {
                baseName = "user";
            }

            var candidate = baseName;
            var suffix = 0;
            while (await context.Users.AnyAsync(u => u.UserName == candidate))
            {
                suffix++;
                candidate = $"{baseName}{suffix}";
            }

            return candidate;
        }
    }
}
