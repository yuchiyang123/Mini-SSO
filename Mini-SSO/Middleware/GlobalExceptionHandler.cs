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

            if (ex is TooManyRequestsException tooMany)
            {
                ctx.Response.Headers.RetryAfter = tooMany.RetryAfterSeconds.ToString();
            }

            var (status, title) = ex switch
            {
                DomainNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
                DomainValidationException => (StatusCodes.Status400BadRequest, "Validation Failed"),
                TooManyRequestsException => (StatusCodes.Status429TooManyRequests, "Too Many Requests"),
                _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
            };

            // Only surface ex.Message for our own well-known exceptions; unexpected
            // exceptions (DB errors, etc.) could leak internal details otherwise.
            var detail =
                ex is DomainNotFoundException or DomainValidationException or TooManyRequestsException
                    ? ex.Message
                    : null;

            var extensions =
                ex is DomainValidationException { Errors.Count: > 0 } validationEx
                    ? new Dictionary<string, object?> { ["errors"] = validationEx.Errors }
                    : null;

            await Results
                .Problem(title: title, statusCode: status, detail: detail, extensions: extensions)
                .ExecuteAsync(ctx);
            return true;
        }
    }
}
