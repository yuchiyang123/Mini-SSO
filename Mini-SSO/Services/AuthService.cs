using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoMapper;
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
    public class AuthService(AuthContext context, IMapper mapper, IConfiguration configuration)
    {
        public readonly IConfiguration _configuration = configuration;

        public async Task<bool> LoginAsync(string userName, string password)
        {
            var users =
                await context.Users.FirstOrDefaultAsync(x => x.UserName == userName)
                ?? throw new DomainValidationException("使用者帳號或是密碼錯誤");
            var hasher = new PasswordHasher<Users>();
            var result = hasher.VerifyHashedPassword(users, users.PasswordHash!, password);
            return result != PasswordVerificationResult.Failed;
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
            var entity = mapper.Map<Users>(userDto);
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
