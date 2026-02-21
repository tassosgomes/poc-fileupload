using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UploadPoc.API.Services;

namespace UploadPoc.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly JwtService _jwtService;

    public AuthController(JwtService jwtService)
    {
        _jwtService = jwtService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request.Username != "admin" || request.Password != "admin123")
        {
            var invalidCredentials = new ProblemDetails
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc9457",
                Title = "Invalid credentials",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Username or password is incorrect.",
                Instance = HttpContext.Request.Path
            };

            return Unauthorized(invalidCredentials);
        }

        var token = _jwtService.GenerateToken(request.Username);
        var expiresAt = DateTime.UtcNow.AddHours(_jwtService.ExpirationHours);

        return Ok(new
        {
            token,
            expiresAt
        });
    }
}

public sealed record LoginRequest(string Username, string Password);
