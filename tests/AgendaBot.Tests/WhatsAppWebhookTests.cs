using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgendaBot.Api.WhatsApp;

namespace AgendaBot.Tests;

[Collection("api")]
public class WhatsAppWebhookTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task La_verificacion_de_Meta_devuelve_el_challenge_solo_con_el_token_correcto()
    {
        var http = api.CreateClient();
        var ok = await http.GetAsync($"/webhook/whatsapp?hub.mode=subscribe&hub.verify_token={ApiFactory.WhatsAppVerifyToken}&hub.challenge=12345");
        Assert.Equal("12345", await ok.Content.ReadAsStringAsync());

        var bad = await http.GetAsync("/webhook/whatsapp?hub.mode=subscribe&hub.verify_token=otro&hub.challenge=12345");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task Rechaza_payloads_sin_firma_o_con_firma_mala()
    {
        var http = api.CreateClient();
        var body = Payload("wamid.1", "hola");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, body, signature: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, body, signature: "sha256=" + new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, body, signature: "sha256=no-es-hex")).StatusCode);
        Assert.Empty(api.Llm.Calls);
    }

    [Fact]
    public async Task Responde_por_WhatsApp_y_no_procesa_dos_veces_el_mismo_mensaje()
    {
        api.Llm.ThenText("¡Hola! ¿En qué te ayudo?");
        var http = api.CreateClient();
        var body = Payload("wamid.repetido", "hola");

        Assert.Equal(HttpStatusCode.OK, (await Post(http, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(http, body)).StatusCode); // reintento de Meta

        var sent = await api.WhatsApp.WaitForAsync(1);
        await Task.Delay(300); // margen por si llegara un segundo envío
        var (url, json) = Assert.Single(api.WhatsApp.Sent);
        Assert.EndsWith("/123456/messages", url);
        Assert.Equal("18095550199", json.GetProperty("to").GetString());
        Assert.EndsWith("\n\n¡Hola! ¿En qué te ayudo?", json.GetProperty("text").GetProperty("body").GetString()); // después del aviso de IA
        Assert.Single(api.Llm.Calls);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Un_audio_recibe_respuesta_fija_sin_llamar_al_modelo()
    {
        var http = api.CreateClient();
        var body = """
            {"entry":[{"changes":[{"value":{"messages":[
              {"id":"wamid.audio","from":"18095550199","type":"audio","audio":{"id":"x"}}]}}]}]}
            """;
        Assert.Equal(HttpStatusCode.OK, (await Post(http, body)).StatusCode);

        var (_, json) = Assert.Single(await api.WhatsApp.WaitForAsync(1));
        Assert.Equal(WhatsAppWebhook.UnsupportedReply, json.GetProperty("text").GetProperty("body").GetString());
        Assert.Empty(api.Llm.Calls);
    }

    [Fact]
    public async Task Los_avisos_de_entregado_y_leido_se_ignoran()
    {
        var http = api.CreateClient();
        var body = """{"entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.x","status":"read"}]}}]}]}""";
        Assert.Equal(HttpStatusCode.OK, (await Post(http, body)).StatusCode);
        await Task.Delay(300);
        Assert.Empty(api.WhatsApp.Sent);
    }

    private static string Payload(string id, string text) => $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "contacts":[{"wa_id":"18095550199","profile":{"name":"Cliente"}}],
          "messages":[{"id":"{{{id}}}","from":"18095550199","timestamp":"1700000000","type":"text","text":{"body":"{{{text}}}"}}]
        }}]}]}
        """;

    private static Task<HttpResponseMessage> Post(HttpClient http, string body, string? signature = "")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/whatsapp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        signature = signature == ""
            ? "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiFactory.WhatsAppSecret), Encoding.UTF8.GetBytes(body)))
            : signature;
        if (signature is not null) req.Headers.Add("X-Hub-Signature-256", signature);
        return http.SendAsync(req);
    }
}
