using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace AgendaBot.Tests;

// Reemplaza a graph.facebook.com: guarda lo que la API intentó mandar y responde 200.
public class FakeWhatsApp : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, JsonElement Body)> Sent { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var json = await request.Content!.ReadAsStringAsync(ct);
        Sent.Enqueue((request.RequestUri!.ToString(), JsonDocument.Parse(json).RootElement.Clone()));
        return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
    }

    // El webhook procesa en segundo plano, así que los tests esperan a que llegue el envío.
    public async Task<List<(string Url, JsonElement Body)>> WaitForAsync(int count, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Sent.Count < count && DateTime.UtcNow < until) await Task.Delay(50);
        return Sent.ToList();
    }
}
