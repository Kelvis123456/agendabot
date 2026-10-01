using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using AgendaBot.Api.Agent;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.WhatsApp;

public record IncomingMessage(string From, string Text);

public static class WhatsAppWebhook
{
    public const string UnsupportedReply = "Por ahora solo puedo leer mensajes de texto. ¿Me escribes lo que necesitas?";

    public static void MapWhatsApp(this IEndpointRouteBuilder app)
    {
        // Meta llama a este GET una sola vez, al registrar el webhook.
        app.MapGet("/webhook/whatsapp", (HttpRequest req, IOptions<WhatsAppOptions> options) =>
        {
            var token = options.Value.VerifyToken;
            var ok = req.Query["hub.mode"] == "subscribe" && token.Length > 0
                     && FixedEquals(req.Query["hub.verify_token"].ToString(), token);
            return ok ? Results.Text(req.Query["hub.challenge"].ToString()) : Results.StatusCode(403);
        });

        app.MapPost("/webhook/whatsapp", async (
            HttpRequest req, IOptions<WhatsAppOptions> options, AppDbContext db, BusinessClock clock,
            Channel<IncomingMessage> queue, PhoneRateLimiter limiter, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("WhatsAppWebhook");
            if (req.ContentLength > 1_000_000) return Results.StatusCode(413);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var body = ms.ToArray();

            if (!ValidSignature(body, req.Headers["X-Hub-Signature-256"].ToString(), options.Value.AppSecret))
                return Results.Unauthorized();

            List<(string Id, string From, string? Text)> messages;
            try
            {
                messages = ParseMessages(body);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log.LogWarning(ex, "Payload de WhatsApp con forma inesperada");
                return Results.BadRequest();
            }

            foreach (var (id, from, text) in messages)
            {
                if (!await FirstTimeAsync(db, id, clock)) continue;
                if (!limiter.TryAcquire(from))
                {
                    log.LogWarning("Mensaje descartado por rate limit de {Phone}", from);
                    continue;
                }
                // Responder 200 rápido y procesar después: Meta reintenta si tardamos.
                await queue.Writer.WriteAsync(new IncomingMessage(from, text ?? ""));
            }

            return Results.Ok();
        });
    }

    public static bool ValidSignature(byte[] body, string header, string appSecret)
    {
        const string prefix = "sha256=";
        if (appSecret.Length == 0 || !header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header[prefix.Length..]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Texto null = el cliente mandó audio, imagen, sticker, etc.
    private static List<(string Id, string From, string? Text)> ParseMessages(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var result = new List<(string Id, string From, string? Text)>();
        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return result;

        foreach (var entry in entries.EnumerateArray())
        foreach (var change in entry.GetProperty("changes").EnumerateArray())
        {
            // Los avisos de "entregado"/"leído" llegan sin "messages" y se ignoran.
            if (!change.GetProperty("value").TryGetProperty("messages", out var messages)) continue;
            foreach (var m in messages.EnumerateArray())
            {
                var text = m.GetProperty("type").GetString() == "text"
                    ? m.GetProperty("text").GetProperty("body").GetString()
                    : null;
                result.Add((m.GetProperty("id").GetString()!, m.GetProperty("from").GetString()!, text));
            }
        }
        return result;
    }

    private static async Task<bool> FirstTimeAsync(AppDbContext db, string id, BusinessClock clock)
    {
        db.ProcessedWhatsAppMessages.Add(new ProcessedWhatsAppMessage { Id = id, ReceivedAt = clock.Now });
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

// Tope de mensajes por número para que nadie dispare la factura del LLM.
public class PhoneRateLimiter(IOptions<WhatsAppOptions> options)
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(phone =>
        RateLimitPartition.GetFixedWindowLimiter(phone, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.MessagesPerPhonePer10Min,
            Window = TimeSpan.FromMinutes(10),
        }));

    public bool TryAcquire(string phone)
    {
        using var lease = _limiter.AttemptAcquire(phone);
        return lease.IsAcquired;
    }
}

// Consume la cola de a un mensaje: llama al agente y manda la respuesta por WhatsApp.
// La cola es en memoria: si la API se cae con mensajes encolados, esos se pierden. Para este
// volumen alcanza; si crece, pasar a una cola persistente.
public class WhatsAppWorker(Channel<IncomingMessage> queue, IServiceScopeFactory scopes, ILogger<WhatsAppWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var msg in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var reply = msg.Text.Length == 0
                    ? WhatsAppWebhook.UnsupportedReply
                    : await scope.ServiceProvider.GetRequiredService<AgentService>().HandleAsync(msg.From, msg.Text, ct);
                await scope.ServiceProvider.GetRequiredService<WhatsAppClient>().SendTextAsync(msg.From, reply, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "No se pudo procesar el mensaje de {Phone}", msg.From);
            }
        }
    }
}
