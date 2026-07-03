using Microsoft.EntityFrameworkCore;
using Mini_SSO.Model.Entities;

namespace Mini_SSO.Services
{
    /// <summary>
    /// RevokedTokens 只需要保留到 token 原本會自然過期的時間點，過了就沒意義了。
    /// 每小時清一次，避免這張表無限長大。
    /// </summary>
    public class RevokedTokenCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<RevokedTokenCleanupService> logger
    ) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AuthContext>();
                    var deleted = await context
                        .RevokedTokens.Where(t => t.ExpiresAt < DateTime.UtcNow)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "Cleaned up {Count} expired revoked token(s).",
                            deleted
                        );
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to clean up expired revoked tokens.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
