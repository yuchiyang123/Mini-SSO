using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Services;

namespace Mini_SSO.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController(AuthService service) : ControllerBase
    {
        [HttpPost()]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (await service.LoginAsync(dto.UserName, dto.Password))
            {
                Guid userId = await service.GetIdByUserName(dto.UserName);
                string token = service.GenerateeToken(userId.ToString());
                Response.Cookies.Append("token", token, new CookieOptions { HttpOnly = true });

                return Ok();
            }
            return BadRequest();
        }

        [HttpPost("logout")]
        [Authorize]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("token");
            return Ok();
        }

        [HttpPost("create")]
        public async Task<ActionResult> Create(CreateUserDto userDto)
        {
            await service.CreateUserAsync(userDto);
            return Ok();
        }

        [HttpGet("valid/username")]
        public async Task<bool> ValidUserName([FromQuery] string username)
        {
            bool isRepeat = await service.ValidUserName(username);
            return isRepeat;
        }
    }
}
