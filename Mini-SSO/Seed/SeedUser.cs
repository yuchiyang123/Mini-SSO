using Microsoft.AspNetCore.Identity;
using Mini_SSO.Model.Entities;

namespace Mini_SSO.Seed
{
    public class SeedUser
    {
        public static async Task SeedUserAsync(AuthContext context, IConfiguration configuration)
        {
            if (context.Users.Any())
                return;

            var enabled = configuration.GetValue<bool?>("Seed:CreateDefaultAdmin") ?? true;
            if (!enabled)
                return;

            var userName = configuration["Seed:AdminUserName"] ?? "admin";
            var password = configuration["Seed:AdminPassword"] ?? "admin123";
            var email = configuration["Seed:AdminEmail"] ?? "123@test.com";

            var hasher = new PasswordHasher<Users>();
            var user = new Users
            {
                UserName = userName,
                PasswordHash = hasher.HashPassword(null!, password),
                Email = email,
            };

            context.Users.Add(user);
            await context.SaveChangesAsync();
        }
    }
}
