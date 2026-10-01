using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.WhatsApp;

public class WhatsAppOptions
{
    // Los cuatro primeros salen de la app en Meta for Developers. Van como secretos, nunca en el repo.
    public string VerifyToken { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string GraphVersion { get; set; } = "v23.0";
    public int MessagesPerPhonePer10Min { get; set; } = 20;
}

// Cliente tipado para la Cloud API. Solo lo que se usa: texto libre (dentro de la ventana de
// 24 h desde el último mensaje del cliente) y plantillas aprobadas (fuera de esa ventana).
public class WhatsAppClient(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<WhatsAppClient> log)
{
    public Task SendTextAsync(string to, string body, CancellationToken ct = default) =>
        SendAsync(new { messaging_product = "whatsapp", to, type = "text", text = new { body } }, ct);

    public Task SendTemplateAsync(string to, string template, string language, IEnumerable<string> parameters, CancellationToken ct = default) =>
        SendAsync(new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = template,
                language = new { code = language },
                components = new[]
                {
                    new { type = "body", parameters = parameters.Select(p => new { type = "text", text = p }).ToArray() },
                },
            },
        }, ct);

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var o = options.Value;
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{o.GraphVersion}/{o.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            log.LogError("WhatsApp respondió {Status}: {Body}", (int)res.StatusCode, body);
            res.EnsureSuccessStatusCode();
        }
    }
}
