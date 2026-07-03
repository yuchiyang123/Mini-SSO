namespace Mini_SSO.Model.Entities
{
    public class Users
    {
        public Guid UserId { get; set; }
        public required string UserName { get; set; }
        public string? PasswordHash { get; set; }
        public required string Email { get; set; }
        public DateTime UpdateAt { get; set; }
        public DateTime CreateAt { get; set; }
        public List<UserLogin> UserLogins { get; set; } = [];
    }
}
