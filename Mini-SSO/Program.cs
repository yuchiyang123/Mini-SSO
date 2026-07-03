using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mini_SSO.Middleware;
using Mini_SSO.Model.Entities;
using Mini_SSO.Seed;
using Mini_SSO.Services;
using Scalar.AspNetCore;
using Serilog;

const string ExternalCookieScheme = "ExternalCookie";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

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
            // 讓「登出」真的能讓 token 失效，而不是只清 Cookie——舊的 token 若被
            // 存下來（例如被複製到別的地方），沒有這段的話在自然過期前都還能用。
            OnTokenValidated = async context =>
            {
                var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (string.IsNullOrEmpty(jti))
                {
                    context.Fail("Token missing jti claim.");
                    return;
                }

                var authService =
                    context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                if (await authService.IsTokenRevokedAsync(jti))
                {
                    context.Fail("Token has been revoked.");
                }
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
builder.Services.AddHostedService<RevokedTokenCleanupService>();

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

// 全站流量保護（跟登入端點專屬的 IP 鎖定是不同層次）：依來源 IP 分桶，
// 每 10 秒最多 30 個請求，可以短暫爆發到 50 個。涵蓋所有沒有另外做鎖定
// 邏輯的端點（例如 /create、/valid/username）。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
            }
        );
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "10";
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new
            {
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "請求過於頻繁，請稍後再試。",
            },
            cancellationToken: cancellationToken
        );
    };
});

builder.Services.AddOpenApi();

var app = builder.Build();

// 必須放在最前面：讓 nginx 轉發過來的 X-Forwarded-For / X-Forwarded-Proto
// 覆寫 Request.Scheme 與 Connection.RemoteIpAddress，否則登入鎖定會鎖到 nginx
// 的 IP，Cookie 的 Secure 判斷（Request.IsHttps）也會誤判成一律是 HTTP。
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// api 容器不會直接對外開 port（見 docker-compose.yml），只有 nginx 進得來，
// 所以這裡可以放心信任任何來源的 X-Forwarded-* header，不用另外設定
// KnownProxies/KnownNetworks 白名單。
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseSerilogRequestLogging();

app.MapOpenApi();
app.MapScalarApiReference();
app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);
app.UseRateLimiter();
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

try
{
    Log.Information("Starting Mini-SSO");
    await app.RunAsync();
}
catch (IOException ex) when (IsAddressInUse(ex))
{
    Log.Fatal(
        "啟動失敗：設定的 Port 已經被其他程式占用，請更換 Port 號後再試一次。"
            + "可透過環境變數 ASPNETCORE_HTTP_PORTS（或 ASPNETCORE_URLS / --urls）指定其他 Port。"
            + "原始錯誤：{Message}",
        ex.Message
    );
    Environment.Exit(1);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Mini-SSO 因未預期的例外而終止");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static bool IsAddressInUse(Exception? ex)
{
    while (ex is not null)
    {
        if (ex is Microsoft.AspNetCore.Connections.AddressInUseException)
            return true;
        ex = ex.InnerException;
    }
    return false;
}

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
