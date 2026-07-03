using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Mini_SSO.Common.Filters
{
    /// <summary>
    /// 簡易版雙提交 Cookie CSRF 驗證：要求 XSRF-TOKEN cookie 的值跟 X-CSRF-TOKEN
    /// header 完全相等。
    ///
    /// 原本用 ASP.NET Core 內建的 IAntiforgery，但它的 cookie 值跟 header 值是刻意
    /// 設計成「不同但配對」（同一把密鑰產生、互相對應，不是字串相等），且會綁定當下
    /// 的登入狀態（claims），這跟大多數前端框架/既有系統的慣例（讀 cookie 值原封不動
    /// 鏡射回 header，單純比對是否相等）對不上，串接第三方前端時會直接撞見
    /// AntiforgeryValidationException。改成這個簡單版本後相容性好很多，也連帶不用再
    /// 要求「登入後要重新拿一次 CSRF token」。
    /// </summary>
    public sealed class ApiAntiforgeryAttribute : Attribute, IAuthorizationFilter
    {
        public const string CookieName = "XSRF-TOKEN";
        public const string HeaderName = "X-CSRF-TOKEN";

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var cookieValue = context.HttpContext.Request.Cookies[CookieName];
            var headerValue = context.HttpContext.Request.Headers[HeaderName].ToString();

            if (
                string.IsNullOrEmpty(cookieValue)
                || string.IsNullOrEmpty(headerValue)
                || !string.Equals(cookieValue, headerValue, StringComparison.Ordinal)
            )
            {
                context.Result = new ObjectResult(
                    new
                    {
                        title = "Invalid CSRF Token",
                        status = StatusCodes.Status400BadRequest,
                        detail = "CSRF token 缺失或不符，請先呼叫 GET /api/auth/csrf 取得新的 token。",
                    }
                )
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
            }
        }
    }
}
