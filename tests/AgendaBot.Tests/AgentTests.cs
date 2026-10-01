using AgendaBot.Api.Agent;
using AgendaBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaBot.Tests;

[Collection("api")]
public class AgentTests(ApiFactory api) : IAsyncLifetime
{
    private const string Phone = "18095550101";
    private static readonly DateOnly Monday = NextMonday();
    private int _corte, _luis;

    public async Task InitializeAsync()
    {
        await api.ResetAsync();
        await using var db = api.CreateDb();
        var corte = new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
        var luis = new Staff { Name = "Luis", WorkingHours = [new() { Day = DayOfWeek.Monday, Open = new(9, 0), Close = new(12, 0) }] };
        db.AddRange(corte, luis);
        await db.SaveChangesAsync();
        (_corte, _luis) = (corte.Id, luis.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string TenAm => Monday.ToDateTime(new(10, 0)).ToString("yyyy-MM-ddTHH:mm");

    private Dictionary<string, object?> ProposeArgs(string? start = null) =>
        new() { ["serviceId"] = _corte, ["staffId"] = _luis, ["start"] = start ?? TenAm };

    [Fact]
    public async Task El_modelo_no_puede_proponer_y_confirmar_en_el_mismo_turno()
    {
        api.Llm
            .ThenTools(("set_name", new() { ["name"] = "Pedro" }), ("propose_appointment", ProposeArgs()), ("confirm_pending", new()))
            .ThenText("Listo, te agendé.");

        await Say("Corte el lunes a las 10 con Luis, soy Pedro");

        Assert.Contains("todavía no respondió", api.Llm.ToolResult("confirm_pending"));
        Assert.Equal(0, await CountAppointments());
    }

    [Fact]
    public async Task Agenda_cuando_el_cliente_confirma_en_el_turno_siguiente()
    {
        api.Llm
            .ThenTools(("set_name", new() { ["name"] = "Pedro" }), ("propose_appointment", ProposeArgs()))
            .ThenText("Corte con Luis el lunes a las 10:00 AM, RD$500. ¿Confirmas?")
            .ThenTools(("confirm_pending", new()))
            .ThenText("¡Listo Pedro! Te esperamos.");

        await Say("Corte el lunes a las 10 con Luis, soy Pedro");
        Assert.Contains("pendiente", api.Llm.ToolResult("propose_appointment"));
        Assert.Equal(0, await CountAppointments());

        var reply = await Say("sí");
        Assert.Equal("¡Listo Pedro! Te esperamos.", reply);
        Assert.Contains("agendada", api.Llm.ToolResult("confirm_pending"));
        Assert.Equal(1, await CountAppointments());

        await using var db = api.CreateDb();
        var conversation = await db.Conversations.Include(c => c.Messages).SingleAsync();
        Assert.Null(conversation.PendingKind);
        Assert.Equal(4, conversation.Messages.Count);
    }

    [Fact]
    public async Task No_deja_proponer_un_horario_ocupado_ni_sin_nombre()
    {
        api.Llm
            .ThenTools(("propose_appointment", ProposeArgs()))
            .ThenText("¿Cómo te llamas?")
            .ThenTools(("set_name", new() { ["name"] = "Ana" }), ("propose_appointment", ProposeArgs(Monday.ToDateTime(new(12, 0)).ToString("yyyy-MM-ddTHH:mm"))))
            .ThenText("Ese horario no está.");

        await Say("Quiero un corte");
        Assert.Contains("nombre", api.Llm.ToolResult("propose_appointment"));

        await Say("Ana, el lunes a las 12");
        Assert.Contains("no está disponible", api.Llm.ToolResult("propose_appointment"));

        await using var db = api.CreateDb();
        Assert.Null((await db.Conversations.SingleAsync()).PendingKind);
    }

    [Fact]
    public async Task Confirmar_sin_propuesta_no_hace_nada()
    {
        api.Llm.ThenTools(("confirm_pending", new())).ThenText("¿Qué quieres confirmar?");
        await Say("sí, confirmo");
        Assert.Contains("No hay nada pendiente", api.Llm.ToolResult("confirm_pending"));
        Assert.Equal(0, await CountAppointments());
    }

    private async Task<string> Say(string text)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AgentService>().HandleAsync(Phone, text)).Text;
    }

    private async Task<int> CountAppointments()
    {
        await using var db = api.CreateDb();
        return await db.Appointments.CountAsync();
    }

    private static DateOnly NextMonday()
    {
        var d = DateOnly.FromDateTime(DateTime.Today).AddDays(7);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d;
    }
}
