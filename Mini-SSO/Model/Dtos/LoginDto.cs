namespace Mini_SSO.Model.Dtos
{
    public class LoginDto
    {
        public required string UserName { get; set; }
        public required string Password { get; set; }
    }

    public class CreateUserDto
    {
        public required string UserName { get; set; }
        public required string Password { get; set; }
    }
}
