using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mini_SSO.Common.Enums;
using Mini_SSO.Common.Exceptions;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Model.Entities;
using Mini_SSO.Services;

namespace Mini_SSO.Tests;

public class AuthServiceTests
{
    private static AuthContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthContext(options);
    }

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "unit-test-super-secret-key-at-least-32-chars",
                    ["Jwt:Issuer"] = "test-issuer",
                    ["Jwt:Audience"] = "test-audience",
                    ["Jwt:ExpireMinutes"] = "60",
                }
            )
            .Build();

    private static AuthService CreateService(AuthContext context) =>
        new(context, CreateConfig());

    private static async Task<Users> SeedPasswordUserAsync(
        AuthContext context,
        string userName = "admin",
        string password = "admin123",
        string email = "admin@test.com"
    )
    {
        var hasher = new PasswordHasher<Users>();
        var user = new Users
        {
            UserName = userName,
            Email = email,
            PasswordHash = hasher.HashPassword(null!, password),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTrue()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context);
        var service = CreateService(context);

        var result = await service.LoginAsync("admin", "admin123", "1.2.3.4");

        Assert.True(result);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ThrowsDomainValidationException()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context);
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.LoginAsync("admin", "wrong-password", "1.2.3.4")
        );
    }

    [Fact]
    public async Task LoginAsync_UnknownUser_ThrowsDomainValidationException()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.LoginAsync("does-not-exist", "whatever", "1.2.3.4")
        );
    }

    [Fact]
    public async Task LoginAsync_SsoOnlyAccountNoPassword_DoesNotThrowNullRef_FailsLikeWrongPassword()
    {
        using var context = CreateContext();
        context.Users.Add(
            new Users
            {
                UserName = "ssoUser",
                Email = "sso@test.com",
                PasswordHash = null,
            }
        );
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.LoginAsync("ssoUser", "anything", "1.2.3.4")
        );
    }

    [Fact]
    public async Task LoginAsync_FiveFailures_LocksOutSixthAttemptEvenWithCorrectPassword()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context);
        var service = CreateService(context);
        const string ip = "9.9.9.9";

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.LoginAsync("admin", "wrong-password", ip)
            );
        }

        var ex = await Assert.ThrowsAsync<TooManyRequestsException>(
            () => service.LoginAsync("admin", "admin123", ip)
        );
        Assert.True(ex.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task LoginAsync_DifferentIps_DoNotShareLockout()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context);
        var service = CreateService(context);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.LoginAsync("admin", "wrong-password", "1.1.1.1")
            );
        }

        // A different IP should be unaffected by 1.1.1.1's lockout.
        var result = await service.LoginAsync("admin", "admin123", "2.2.2.2");
        Assert.True(result);
    }

    [Fact]
    public async Task LoginAsync_SuccessResetsFailedCount()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context);
        var service = CreateService(context);
        const string ip = "3.3.3.3";

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.LoginAsync("admin", "wrong-password", ip)
            );
        }

        await service.LoginAsync("admin", "admin123", ip);

        var attempt = await context.LoginAttempts.SingleAsync(a => a.IpAddress == ip);
        Assert.Equal(0, attempt.FailedCount);
        Assert.Null(attempt.LockedUntil);
    }

    [Fact]
    public async Task ExternalLoginAsync_NewProviderKey_CreatesNewUserWithoutPassword()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        var userId = await service.ExternalLoginAsync(
            ProviderEnums.Google,
            "google-sub-123",
            "newuser@test.com",
            "New User"
        );

        var user = await context.Users.SingleAsync(u => u.UserId == userId);
        Assert.Null(user.PasswordHash);
        Assert.Equal("newuser@test.com", user.Email);

        var login = await context.UserLogins.SingleAsync(l => l.UserId == userId);
        Assert.Equal(ProviderEnums.Google, login.Provider);
        Assert.Equal("google-sub-123", login.ProviderKey);
    }

    [Fact]
    public async Task ExternalLoginAsync_ExistingBinding_ReturnsSameUserWithoutDuplicating()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        var firstUserId = await service.ExternalLoginAsync(
            ProviderEnums.GitHub,
            "gh-456",
            "user@test.com",
            "User"
        );
        var secondUserId = await service.ExternalLoginAsync(
            ProviderEnums.GitHub,
            "gh-456",
            "user@test.com",
            "User"
        );

        Assert.Equal(firstUserId, secondUserId);
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(1, await context.UserLogins.CountAsync());
    }

    [Fact]
    public async Task ExternalLoginAsync_ExistingEmailFromPasswordAccount_LinksInsteadOfDuplicating()
    {
        using var context = CreateContext();
        var existing = await SeedPasswordUserAsync(context, email: "shared@test.com");
        var service = CreateService(context);

        var userId = await service.ExternalLoginAsync(
            ProviderEnums.Google,
            "google-sub-999",
            "shared@test.com",
            "Shared"
        );

        Assert.Equal(existing.UserId, userId);
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.NotNull((await context.Users.SingleAsync()).PasswordHash); // still a password account
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsExpectedShape()
    {
        using var context = CreateContext();
        var user = await SeedPasswordUserAsync(context);
        var service = CreateService(context);

        var dto = await service.GetCurrentUserAsync(user.UserId);

        Assert.Equal(user.UserId, dto.UserId);
        Assert.Equal(user.UserName, dto.UserName);
        Assert.True(dto.HasPassword);
        Assert.Empty(dto.LinkedProviders);
    }

    [Fact]
    public async Task GetCurrentUserAsync_UnknownUser_ThrowsDomainNotFoundException()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => service.GetCurrentUserAsync(Guid.NewGuid())
        );
    }

    [Fact]
    public async Task ValidUserName_AvailableVsTaken()
    {
        using var context = CreateContext();
        await SeedPasswordUserAsync(context, userName: "taken");
        var service = CreateService(context);

        Assert.False(await service.ValidUserName("taken"));
        Assert.True(await service.ValidUserName("free"));
    }

    [Fact]
    public async Task CreateUserAsync_HashesPassword()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await service.CreateUserAsync(
            new CreateUserDto
            {
                UserName = "newbie",
                Password = "P@ssw0rd",
                Email = "newbie@test.com",
            }
        );

        var user = await context.Users.SingleAsync(u => u.UserName == "newbie");
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual("P@ssw0rd", user.PasswordHash);
    }

    [Fact]
    public void GenerateeToken_ProducesTokenContainingUserIdClaim()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        var token = service.GenerateeToken(userId);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(token);
        Assert.Equal(userId, parsed.Subject);
        Assert.Contains(parsed.Claims, c => c.Type == "jti");
    }

    [Fact]
    public async Task RevokeTokenAsync_IsIdempotent_AndReflectedByIsTokenRevokedAsync()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var jti = Guid.NewGuid().ToString();

        Assert.False(await service.IsTokenRevokedAsync(jti));

        await service.RevokeTokenAsync(jti, DateTime.UtcNow.AddHours(1));
        await service.RevokeTokenAsync(jti, DateTime.UtcNow.AddHours(1)); // should not throw / duplicate

        Assert.True(await service.IsTokenRevokedAsync(jti));
        Assert.Equal(1, await context.RevokedTokens.CountAsync(t => t.Jti == jti));
    }
}
