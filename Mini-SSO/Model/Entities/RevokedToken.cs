namespace Mini_SSO.Model.Entities
{
    /// <summary>
    /// 已撤銷（登出）的 JWT，以 jti（JWT ID）為主鍵。
    /// ExpiresAt 記錄這個 token 原本什麼時候會自然過期，過了這個時間點
    /// 就算沒被撤銷也已經失效，可以安全清掉，見 RevokedTokenCleanupService。
    /// </summary>
    public class RevokedToken
    {
        public required string Jti { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
