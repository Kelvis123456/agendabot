using System.Net;

namespace AgendaBot.Tests;

[Collection("api")]
public class PublicSiteTests(ApiFactory api)
{
    [Fact]
    public async Task Rutas_inexistentes_dan_404_con_pagina_o_con_json()
    {
        var http = api.CreateClient();
        var page = new HttpRequestMessage(HttpMethod.Get, "/no-existe");
        page.Headers.Accept.ParseAdd("text/html");
        var html = await http.SendAsync(page);
        Assert.Equal(HttpStatusCode.NotFound, html.StatusCode);
        Assert.Contains("Esta página no existe", await html.Content.ReadAsStringAsync());

        var json = await http.GetAsync("/api/no-existe");
        Assert.Equal(HttpStatusCode.NotFound, json.StatusCode);
        Assert.Equal("application/problem+json", json.Content.Headers.ContentType?.MediaType);
    }
}
