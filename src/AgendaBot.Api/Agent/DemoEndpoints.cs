using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace AgendaBot.Api.Agent;

public record DemoMessage([Required] Guid Session, [Required, MaxLength(1000)] string Text);

// Chat público para probar el agente sin WhatsApp. Cada pestaña del navegador es un cliente
// distinto: la sesión se convierte en un número falso que empieza con 999 (no es un prefijo real).
public static class DemoEndpoints
{
    public static void MapDemo(this IEndpointRouteBuilder app)
    {
        app.MapPost("/demo/chat", async (DemoMessage msg, AgentService agent, CancellationToken ct) =>
        {
            if (msg.Session == Guid.Empty || string.IsNullOrWhiteSpace(msg.Text)) return Results.BadRequest();
            var reply = await agent.HandleAsync(FakePhone(msg.Session), msg.Text, ct);
            return Results.Ok(new
            {
                reply = reply.Text,
                tools = reply.Tools.Select(t => new { t.Name, t.Arguments, t.Failed, t.Result }),
            });
        }).RequireRateLimiting("demo");
    }

    public static string FakePhone(Guid session)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(session.ToString()));
        var digits = new StringBuilder("999");
        foreach (var b in hash.AsSpan(0, 12)) digits.Append(b % 10);
        return digits.ToString();
    }
}
