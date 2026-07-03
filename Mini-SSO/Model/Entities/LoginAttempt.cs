namespace Mini_SSO.Model.Entities
{
    /// <summary>
    /// 依來源 IP 追蹤登入失敗次數，超過門檻後鎖定一段時間，防暴力破解。
    /// </summary>
    public class LoginAttempt
    {
        public required string IpAddress { get; set; }
        public int FailedCount { get; set; }
        public DateTime? LockedUntil { get; set; }
        public DateTime LastAttemptAt { get; set; }
    }
}
