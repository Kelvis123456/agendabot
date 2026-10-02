using System.Net;
using System.Net.Http.Json;
using AgendaBot.Api.Agent;
using AgendaBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaBot.Tests;

// La política de privacidad promete borrar los datos de la demo a los 7 días o al reiniciar.
[Collection("api")]
public class DemoDataTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Empezar_de_nuevo_borra_todo_lo_de_esa_sesion_y_nada_mas()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        await Seed(DemoEndpoints.FakePhone(mine), daysAgo: 0);
        await Seed(DemoEndpoints.FakePhone(other), daysAgo: 0);

        var res = await api.CreateClient().PostAsJsonAsync("/demo/reset", new { session = mine });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        await using var db = api.CreateDb();
        Assert.Equal([DemoEndpoints.FakePhone(other)], await db.Customers.Select(c => c.Phone).ToListAsync());
        Assert.Equal(1, await db.Conversations.CountAsync());
        Assert.Equal(1, await db.ConversationMessages.CountAsync());
        Assert.Equal(1, await db.Appointments.CountAsync());
    }

    [Fact]
    public async Task La_limpieza_borra_solo_clientes_de_la_demo_sin_actividad_en_7_dias()
    {
        await Seed("999000000000001", daysAgo: 8);              // demo vieja: se borra
        await Seed("999000000000002", daysAgo: 8, activeDaysAgo: 1); // demo vieja pero escribió ayer: se queda
        await Seed("999000000000003", daysAgo: 2);              // demo reciente: se queda
        await Seed("18095550001", daysAgo: 30);                 // cliente real de WhatsApp: nunca se toca

        using (var scope = api.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<DemoData>().DeleteExpiredAsync());

        await using var db = api.CreateDb();
        var left = await db.Customers.OrderBy(c => c.Phone).Select(c => c.Phone).ToListAsync();
        Assert.Equal(["18095550001", "999000000000002", "999000000000003"], left);
        Assert.Equal(3, await db.Appointments.CountAsync());
    }

    [Fact]
    public async Task Reset_sin_sesion_es_400()
    {
        var res = await api.CreateClient().PostAsJsonAsync("/demo/reset", new { session = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task La_pagina_de_privacidad_nombra_a_quien_recibe_los_mensajes()
    {
        var html = await api.CreateClient().GetStringAsync("/privacidad.html");
        Assert.Contains("Google (Gemini API)", html);
        Assert.Contains("7 días", html);
    }

    private async Task Seed(string phone, int daysAgo, int? activeDaysAgo = null)
    {
        await using var db = api.CreateDb();
        var created = DateTime.Now.AddDays(-daysAgo);
        var service = await db.Services.FirstOrDefaultAsync() ?? new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
        var staff = await db.Staff.FirstOrDefaultAsync() ?? new Staff { Name = "Luis" };
        var customer = new Customer { Phone = phone, Name = "Prueba", CreatedAt = created };
        var lastActivity = DateTime.Now.AddDays(-(activeDaysAgo ?? daysAgo));
        db.Add(new Conversation
        {
            Customer = customer, UpdatedAt = lastActivity,
            Messages = [new ConversationMessage { Role = "user", Text = "hola", CreatedAt = lastActivity }],
        });
        db.Add(new Appointment
        {
            Customer = customer, Service = service, Staff = staff,
            Start = DateTime.Today.AddDays(3).AddHours(10), End = DateTime.Today.AddDays(3).AddHours(10.5), CreatedAt = created,
        });
        await db.SaveChangesAsync();
    }
}
