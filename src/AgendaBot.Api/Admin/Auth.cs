using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgendaBot.Api.Admin;

public class AuthOptions
{
    // Un solo dueño por ahora. Ambos valores vienen de secretos/variables de entorno.
    public string AdminPassword { get; set; } = "";
    public string JwtKey { get; set; } = "";
    public int TokenHours { get; set; } = 8;
}

public record LoginRequest([Required] string Password);

public static class Auth
{
    public static SymmetricSecurityKey SigningKey(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.JwtKey));

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", (LoginRequest req, IOptions<AuthOptions> options) =>
        {
            var o = options.Value;
            var ok = o.AdminPassword.Length > 0 && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(req.Password), Encoding.UTF8.GetBytes(o.AdminPassword));
            if (!ok) return Results.Unauthorized();

            var token = new JwtSecurityToken(
                claims: [new Claim(ClaimTypes.Role, "admin")],
                expires: DateTime.UtcNow.AddHours(o.TokenHours),
                signingCredentials: new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256));
            return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
        }).RequireRateLimiting("login");
    }
}
