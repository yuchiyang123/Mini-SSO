using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mini_SSO.Common.Enums;
using Mini_SSO.Common.Filters;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Services;

namespace Mini_SSO.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController(
        AuthService service,
        IConfiguration configuration,
        ExternalProviderAvailability providerAvailability
    ) : ControllerBase
    {
        private const string ExternalCookieScheme = "ExternalCookie";
        private const string RefreshCookieName = "refresh_token";
        private const string RefreshCookiePath = "/api/auth";

        private static readonly Dictionary<string, ProviderEnums> ExternalProviders = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["google"] = ProviderEnums.Google,
            ["github"] = ProviderEnums.GitHub,
        };

        /// <summary>
        /// 取得 CSRF token（雙提交 cookie 模式）。前端進站時先呼叫這支，把回傳的
        /// csrfToken 放進後續 POST 請求的 X-CSRF-TOKEN header（跟 XSRF-TOKEN cookie
        /// 的值必須完全相等，驗證邏輯見 ApiAntiforgeryAttribute）。
        /// </summary>
        [HttpGet("csrf")]
        public IActionResult GetCsrfToken()
        {
            // Hex, not Base64: Base64's `/`/`+`/`=` get percent-encoded by ASP.NET Core
            // when written into the Set-Cookie header, but the JSON body below returns
            // the raw (unescaped) value. A frontend that reads document.cookie directly
            // (browsers never auto-decode it) would then send the percent-encoded form
            // as the header while the cookie compares against the decoded form — a
            // guaranteed mismatch. Hex has no characters that ever need encoding.
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

            Response.Cookies.Append(
                ApiAntiforgeryAttribute.CookieName,
                token,
                new CookieOptions
                {
                    HttpOnly = false,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                }
            );

            return Ok(new { csrfToken = token });
        }

        [HttpPost()]
        [ApiAntiforgery]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (await service.LoginAsync(dto.UserName, dto.Password, GetClientIp()))
            {
                Guid userId = await service.GetIdByUserName(dto.UserName);
                string token = service.GenerateeToken(userId.ToString());
                string refreshToken = await service.GenerateRefreshTokenAsync(userId);
                AppendAuthCookie(token);
                AppendRefreshCookie(refreshToken);

                return Ok();
            }
            return BadRequest();
        }

        /// <summary>
        /// 用 refresh token（存在 HttpOnly cookie）換一組新的 access token + refresh
        /// token。access token 過期後前端呼叫這支，不需要使用者重新輸入帳密。
        /// </summary>
        [HttpPost("refresh")]
        [ApiAntiforgery]
        public async Task<IActionResult> Refresh()
        {
            var refreshToken = Request.Cookies[RefreshCookieName];
            if (string.IsNullOrEmpty(refreshToken))
            {
                return Unauthorized();
            }

            var tokens = await service.RefreshAsync(refreshToken);
            if (tokens is null)
            {
                Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = RefreshCookiePath });
                return Unauthorized();
            }

            AppendAuthCookie(tokens.AccessToken);
            AppendRefreshCookie(tokens.RefreshToken);
            return Ok();
        }

        [HttpPost("logout")]
        [Authorize]
        [ApiAntiforgery]
        public async Task<IActionResult> Logout()
        {
            var jti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var expClaim = User.FindFirstValue(JwtRegisteredClaimNames.Exp);

            if (!string.IsNullOrEmpty(jti) && long.TryParse(expClaim, out var expUnixSeconds))
            {
                await service.RevokeTokenAsync(
                    jti,
                    DateTimeOffset.FromUnixTimeSeconds(expUnixSeconds).UtcDateTime
                );
            }

            var refreshToken = Request.Cookies[RefreshCookieName];
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await service.RevokeRefreshTokenAsync(refreshToken);
            }

            Response.Cookies.Delete("token");
            Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = RefreshCookiePath });
            return Ok();
        }

        [HttpPost("create")]
        [ApiAntiforgery]
        public async Task<ActionResult> Create(CreateUserDto userDto)
        {
            await service.CreateUserAsync(userDto);
            return Ok();
        }

        [HttpGet("valid/username")]
        public async Task<bool> ValidUserName([FromQuery] string username)
        {
            bool isRepeat = await service.ValidUserName(username);
            return isRepeat;
        }

        /// <summary>
        /// 取得目前登入者的基本資料（含已綁定的第三方登入 provider）。
        /// </summary>
        [HttpGet("me")]
        [Authorize]
        public async Task<ActionResult<CurrentUserDto>> Me()
        {
            var userIdClaim =
                User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized();
            }

            var currentUser = await service.GetCurrentUserAsync(userId);
            return Ok(currentUser);
        }

        /// <summary>
        /// 觸發第三方登入，導向 provider 的授權頁面。provider: google / github。
        /// </summary>
        [HttpGet("external/{provider}/login")]
        public IActionResult ExternalLogin(string provider)
        {
            if (!ExternalProviders.TryGetValue(provider, out var providerEnum))
            {
                return BadRequest($"Unsupported provider '{provider}'.");
            }

            if (!providerAvailability.IsEnabled(providerEnum.ToString()))
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    $"'{provider}' login is not configured yet. Please contact the administrator."
                );
            }

            var callbackUrl = Url.Action(
                nameof(ExternalLoginCallback),
                "Auth",
                new { provider },
                Request.Scheme
            );

            var properties = new AuthenticationProperties { RedirectUri = callbackUrl };
            return Challenge(properties, providerEnum.ToString());
        }

        /// <summary>
        /// 第三方登入完成後的回呼：讀取暫存 cookie 中的 claims，找到（或建立）本地帳號，
        /// 簽發本地 JWT cookie，再導回前端頁面。
        /// </summary>
        [HttpGet("external/{provider}/callback")]
        public async Task<IActionResult> ExternalLoginCallback(string provider)
        {
            if (!ExternalProviders.TryGetValue(provider, out var providerEnum))
            {
                return BadRequest($"Unsupported provider '{provider}'.");
            }

            var result = await HttpContext.AuthenticateAsync(ExternalCookieScheme);
            await HttpContext.SignOutAsync(ExternalCookieScheme);

            if (!result.Succeeded || result.Principal is null)
            {
                return Unauthorized("External authentication failed.");
            }

            var providerKey = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = result.Principal.FindFirstValue(ClaimTypes.Email);
            var name =
                result.Principal.FindFirstValue(ClaimTypes.Name)
                ?? result.Principal.FindFirstValue("name");

            if (string.IsNullOrEmpty(providerKey) || string.IsNullOrEmpty(email))
            {
                return BadRequest(
                    "The provider did not return the required profile information (id/email)."
                );
            }

            var userId = await service.ExternalLoginAsync(providerEnum, providerKey, email, name);
            var token = service.GenerateeToken(userId.ToString());
            var refreshToken = await service.GenerateRefreshTokenAsync(userId);
            AppendAuthCookie(token);
            AppendRefreshCookie(refreshToken);

            var frontendUrl = configuration["Frontend:RedirectUrl"];
            return string.IsNullOrWhiteSpace(frontendUrl) ? Ok() : Redirect(frontendUrl);
        }

        private void AppendAuthCookie(string token)
        {
            Response.Cookies.Append(
                "token",
                token,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                }
            );
        }

        private void AppendRefreshCookie(string refreshToken)
        {
            var refreshDays = configuration.GetValue("Jwt:RefreshExpireDays", 30);

            Response.Cookies.Append(
                RefreshCookieName,
                refreshToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    // 限縮到 /api/auth/* 才會帶上這個 cookie，減少不必要的曝露面。
                    Path = RefreshCookiePath,
                    Expires = DateTimeOffset.UtcNow.AddDays(refreshDays),
                }
            );
        }

        private string GetClientIp() =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
