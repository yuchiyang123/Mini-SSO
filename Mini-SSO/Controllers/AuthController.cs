using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Model.Entities;
using Mini_SSO.Services;

namespace Mini_SSO.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController(AuthService service) : ControllerBase
    {
        [HttpPost()]
        public async Task<ActionResult<string>> Login([FromBody] LoginDto dto)
        {
            if (await service.LoginAsync(dto.UserName, dto.Password))
            {
                Guid userId = await service.GetIdByUserName(dto.UserName);
                string token = service.GenerateeToken(userId.ToString());
                Response.Cookies.Append("token", token, new CookieOptions { HttpOnly = true });

                return Ok(token);
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
            var valid = await service.ValidUserName(userDto.UserName);
            if (!valid)
                return BadRequest();
            await service.CreateUserAsync(userDto);
            return Ok();
        }
    }
}
