using Microsoft.AspNetCore.Identity;
using Mini_SSO.Model.Entities;

namespace Mini_SSO.Seed
{
    public class SeedUser
    {
        public static async Task SeedUserAsync(AuthContext context)
        {
            if (context.Users.Any())
                return;

            var hasher = new PasswordHasher<Users>();
            var user = new Users
            {
                UserName = "admin",
                PasswordHash = hasher.HashPassword(null!, "admin123"),
                Email = "123@test.com",
            };

            context.Users.Add(user);
            await context.SaveChangesAsync();
        }
    }
}
