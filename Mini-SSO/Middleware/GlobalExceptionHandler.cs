using Microsoft.AspNetCore.Diagnostics;
using Mini_SSO.Common.Exceptions;

namespace Mini_SSO.Middleware
{
    public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
        : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext ctx,
            Exception ex,
            CancellationToken ct
        )
        {
            logger.LogError(ex, "Unhandled exception for {Path}", ctx.Request.Path);

            var (status, title) = ex switch
            {
                DomainNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
                DomainValidationException => (StatusCodes.Status400BadRequest, "Validation Failed"),
                _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
            };

            await Results.Problem(title: title, statusCode: status).ExecuteAsync(ctx);
            return true;
        }
    }
}
