namespace Mini_SSO.Model.Dtos
{
    public class CurrentUserDto
    {
        public required Guid UserId { get; set; }
        public required string UserName { get; set; }
        public required string Email { get; set; }
        public bool HasPassword { get; set; }
        public List<string> LinkedProviders { get; set; } = [];
    }
}
