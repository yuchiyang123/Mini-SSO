using Microsoft.Extensions.Caching.Distributed;

namespace Mini_SSO.Services
{
    /// <summary>
    /// 用 Redis（透過 IDistributedCache）存兩種東西：
    /// 1. 已撤銷的 JWT jti 黑名單——靠 Redis 的 TTL 自動過期清理，不用像原本 SQL
    ///    版本那樣另外寫一個背景清理服務。
    /// 2. Refresh token -> UserId 的對應，同樣用 TTL 控制有效期。
    /// </summary>
    public class TokenStore(IDistributedCache cache)
    {
        private const string RevokedPrefix = "mini-sso:revoked:";
        private const string RefreshPrefix = "mini-sso:refresh:";

        public Task RevokeAccessTokenAsync(string jti, DateTime expiresAtUtc)
        {
            var ttl = expiresAtUtc - DateTime.UtcNow;
            if (ttl <= TimeSpan.Zero)
            {
                ttl = TimeSpan.FromSeconds(1);
            }

            return cache.SetStringAsync(
                RevokedPrefix + jti,
                "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }
            );
        }

        public async Task<bool> IsAccessTokenRevokedAsync(string jti) =>
            await cache.GetStringAsync(RevokedPrefix + jti) is not null;

        public Task StoreRefreshTokenAsync(string refreshToken, Guid userId, TimeSpan ttl) =>
            cache.SetStringAsync(
                RefreshPrefix + refreshToken,
                userId.ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }
            );

        public async Task<Guid?> GetUserIdForRefreshTokenAsync(string refreshToken)
        {
            var value = await cache.GetStringAsync(RefreshPrefix + refreshToken);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }

        public Task RevokeRefreshTokenAsync(string refreshToken) =>
            cache.RemoveAsync(RefreshPrefix + refreshToken);
    }
}
