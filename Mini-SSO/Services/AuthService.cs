using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
    }
}
