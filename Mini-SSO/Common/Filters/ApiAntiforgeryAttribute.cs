using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Mini_SSO.Common.Filters
{
    /// <summary>
    /// 手動呼叫 IAntiforgery 驗證 CSRF token。
    /// 內建的 [ValidateAntiForgeryToken] 依賴 MVC Views 才會註冊的過濾器服務，
    /// 這個專案是純 API（AddControllers，沒有 AddControllersWithViews），所以要自己寫，
    /// 並刻意取跟內建屬性不同的名字（ApiAntiforgery）避免混淆。
    /// </summary>
    public sealed class ApiAntiforgeryAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
    }
}
