using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mini_SSO.Middleware;
using Mini_SSO.Model.Entities;
using Mini_SSO.Seed;
using Mini_SSO.Services;
using Scalar.AspNetCore;

const string ExternalCookieScheme = "ExternalCookie";

var builder = WebApplication.CreateBuilder(args);

var jwtConfig = builder.Configuration.GetSection("Jwt");
var key = Encoding.UTF8.GetBytes(jwtConfig["Key"]!);

var googleConfig = builder.Configuration.GetSection("Authentication:Google");
var gitHubConfig = builder.Configuration.GetSection("Authentication:GitHub");

var isGoogleConfigured =
    !string.IsNullOrWhiteSpace(googleConfig["ClientId"])
    && !string.IsNullOrWhiteSpace(googleConfig["ClientSecret"]);
var isGitHubConfigured =
    !string.IsNullOrWhiteSpace(gitHubConfig["ClientId"])
    && !string.IsNullOrWhiteSpace(gitHubConfig["ClientSecret"]);

// Providers without a ClientId/ClientSecret are intentionally left unregistered:
// ASP.NET Core validates RemoteAuthenticationOptions on every request (not just on
// challenge), so registering a provider with empty credentials would 500 every request.
builder.Services.AddSingleton(
    new ExternalProviderAvailability(isGoogleConfigured, isGitHubConfigured)
);

var authBuilder = builder
    .Services.AddAuthentication(option =>
    {
        option.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        option.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(option =>
    {
        option.TokenValidationParameters =
            new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtConfig["Issuer"],
                ValidAudience = jwtConfig["Audience"],
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key),
            };

        option.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                context.Token = context.Request.Cookies["token"];
                return Task.CompletedTask;
            },
        };
    })
    // Temporary sign-in scheme used only to carry the external provider's claims
    // from the callback into our controller, where we mint our own JWT and discard it.
    .AddCookie(
        ExternalCookieScheme,
        option =>
        {
            option.Cookie.Name = "external_sso";
            option.Cookie.HttpOnly = true;
            option.Cookie.SameSite = SameSiteMode.Lax;
            option.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        }
    );

if (isGoogleConfigured)
{
    authBuilder.AddGoogle(
        "Google",
        option =>
        {
            option.SignInScheme = ExternalCookieScheme;
            option.ClientId = googleConfig["ClientId"]!;
            option.ClientSecret = googleConfig["ClientSecret"]!;
            option.CallbackPath = "/signin-google";
            option.SaveTokens = false;
        }
    );
}

if (isGitHubConfigured)
{
    authBuilder.AddGitHub(
        "GitHub",
        option =>
        {
            option.SignInScheme = ExternalCookieScheme;
            option.ClientId = gitHubConfig["ClientId"]!;
            option.ClientSecret = gitHubConfig["ClientSecret"]!;
            option.CallbackPath = "/signin-github";
            option.SaveTokens = false;
            option.Scope.Add("user:email");

            // GitHub only returns `email` on /user when the user made it public,
            // so fall back to the dedicated emails endpoint when it's missing.
            option.Events.OnCreatingTicket = async context =>
            {
                if (context.Identity?.FindFirst(ClaimTypes.Email) is not null)
                    return;

                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://api.github.com/user/emails"
                );
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    context.AccessToken
                );
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github+json")
                );
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mini-SSO", "1.0"));

                using var response = await context.Backchannel.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.HttpContext.RequestAborted
                );
                if (!response.IsSuccessStatusCode)
                    return;

                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var entry in payload.RootElement.EnumerateArray())
                {
                    if (
                        entry.TryGetProperty("primary", out var primary)
                        && primary.GetBoolean()
                        && entry.TryGetProperty("verified", out var verified)
                        && verified.GetBoolean()
                        && entry.TryGetProperty("email", out var email)
                    )
                    {
                        context.Identity?.AddClaim(
                            new Claim(ClaimTypes.Email, email.GetString() ?? string.Empty)
                        );
                        break;
                    }
                }
            };
        }
    );
}

builder.Services.AddDbContext<AuthContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
);

builder.Services.AddHealthChecks().AddDbContextCheck<AuthContext>();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "XSRF-TOKEN";
    options.Cookie.HttpOnly = false;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddScoped<AuthService>();

const string FrontendCorsPolicy = "Frontend";
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        FrontendCorsPolicy,
        policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                // AllowCredentials is required so the browser will send/receive the
                // HttpOnly JWT cookie; it cannot be combined with AllowAnyOrigin.
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
        }
    );
});

builder.Services.AddControllers();
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);
app.UseAuthentication();
app.UseExceptionHandler();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AuthContext>();
    await context.Database.MigrateAsync();
    await SeedUser.SeedUserAsync(context, app.Configuration);
}

app.Use(
    async (context, next) =>
    {
        var headers = context.Response.Headers;

        headers.Append("Cross-Origin-Embedder-Policy", "require-corp");
        headers.Append("Cross-Origin-Opener-Policy", "same-origin");
        headers.Append("Cross-Origin-Resource-Policy", "same-origin");

        headers.Append("X-Content-Type-Options", "nosniff");
        headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.Append(
            "Permissions-Policy",
            "camera=(), microphone=(), geolocation=(), payment=()"
        );

        headers.Append(
            "Content-Security-Policy",
            "default-src 'self'; "
                + "script-src 'self'; "
                + "style-src 'self'; "
                + "img-src 'self' data:; "
                + "font-src 'self'; "
                + "connect-src 'self'; "
                + "object-src 'none'; "
                + "base-uri 'self'; "
                + "frame-ancestors 'none'"
        );

        // ������ݸ�T
        headers.Remove("Server");
        headers.Remove("X-Powered-By");

        await next();
    }
);
app.Run();

public record ExternalProviderAvailability(bool Google, bool GitHub)
{
    public bool IsEnabled(string providerName) =>
        providerName switch
        {
            "Google" => Google,
            "GitHub" => GitHub,
            _ => false,
        };
}
