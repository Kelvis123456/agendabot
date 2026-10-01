using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgendaBot.Tests;

[Collection("api")]
public class DemoTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task La_demo_devuelve_la_respuesta_y_la_traza_de_herramientas()
    {
        api.Llm.ThenTools(("confirm_pending", new())).ThenText("¿Qué quieres confirmar?");

        var res = await api.CreateClient().PostAsJsonAsync("/demo/chat", new { session = Guid.NewGuid(), text = "sí" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("¿Qué quieres confirmar?", body.GetProperty("reply").GetString());
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("confirm_pending", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("failed").GetBoolean());
    }

    [Fact]
    public async Task Rechaza_mensajes_vacios_o_sin_sesion()
    {
        var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/demo/chat", new { session = Guid.NewGuid(), text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/demo/chat", new { text = "hola" })).StatusCode);
        Assert.Empty(api.Llm.Calls);
    }

    [Fact]
    public async Task Sirve_la_pagina_de_la_demo()
    {
        var html = await api.CreateClient().GetStringAsync("/");
        Assert.Contains("Ticket del backend", html);
    }
}
