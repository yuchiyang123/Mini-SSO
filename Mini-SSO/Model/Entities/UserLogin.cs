using Mini_SSO.Common.Enums;

namespace Mini_SSO.Model.Entities
{
    public class UserLogin
    {
        /// <summary>
        /// 外部登入的來源
        /// </summary>
        public ProviderEnums Provider { get; set; }

        /// <summary>
        /// 外部登入的辨識碼
        /// </summary>
        public required string ProviderKey { get; set; }
        public Guid UserId { get; set; }
        public Users User { get; set; }
    }
}
