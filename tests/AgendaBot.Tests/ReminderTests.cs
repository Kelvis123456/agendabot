using AgendaBot.Api.Data;
using AgendaBot.Api.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaBot.Tests;

[Collection("api")]
public class ReminderTests(ApiFactory api) : IAsyncLifetime
{
    private int _soon;

    public async Task InitializeAsync()
    {
        await api.ResetAsync();
        await using var db = api.CreateDb();
        var service = new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
        var staff = new Staff { Name = "Luis" };
        var ana = new Customer { Phone = "18095550301", Name = "Ana", CreatedAt = DateTime.Now, RemindersOptInAt = DateTime.Now };
        db.AddRange(service, staff, ana);
        await db.SaveChangesAsync();

        // Las fechas son relativas a la hora real; la ventana es de 24 h.
        var now = DateTime.Now;
        Appointment At(double hoursFromNow, AppointmentStatus status = AppointmentStatus.Confirmed, double createdHoursAgo = 48) => new()
        {
            StaffId = staff.Id,
            ServiceId = service.Id,
            CustomerId = ana.Id,
            Status = status,
            Start = now.AddHours(hoursFromNow),
            End = now.AddHours(hoursFromNow).AddMinutes(30),
            CreatedAt = now.AddHours(-createdHoursAgo),
        };
        var soon = At(20);
        db.AddRange(soon,
            At(72),                                  // falta mucho
            At(21, AppointmentStatus.Cancelled),     // cancelada
            At(5, createdHoursAgo: 0.5));            // la acaba de reservar
        await db.SaveChangesAsync();
        _soon = soon.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Manda_un_solo_recordatorio_a_las_citas_de_las_proximas_24_horas()
    {
        Assert.Equal(1, await Run());
        Assert.Equal(0, await Run()); // la segunda vuelta no repite

        var (_, json) = Assert.Single(api.WhatsApp.Sent);
        Assert.Equal("template", json.GetProperty("type").GetString());
        Assert.Equal("18095550301", json.GetProperty("to").GetString());
        var parameters = json.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal("Ana", parameters[0].GetProperty("text").GetString());
        Assert.Equal("Corte", parameters[1].GetProperty("text").GetString());

        await using var db = api.CreateDb();
        var reminded = await db.Appointments.Where(a => a.ReminderSentAt != null).Select(a => a.Id).ToListAsync();
        Assert.Equal([_soon], reminded);

        // Queda en la conversación para que el agente entienda un "cancela" como respuesta.
        var msg = await db.ConversationMessages.SingleAsync();
        Assert.Contains($"id {_soon}", msg.Text);
    }

    [Fact]
    public async Task Si_WhatsApp_falla_no_se_marca_y_se_reintenta_despues()
    {
        api.WhatsApp.Status = System.Net.HttpStatusCode.InternalServerError;
        Assert.Equal(0, await Run());
        await using (var db = api.CreateDb())
            Assert.Null((await db.Appointments.FindAsync(_soon))!.ReminderSentAt);

        api.WhatsApp.Status = System.Net.HttpStatusCode.OK;
        Assert.Equal(1, await Run());
    }

    [Fact]
    public async Task No_le_escribe_a_quien_no_acepto_o_se_dio_de_baja()
    {
        await using (var db = api.CreateDb())
            await db.Customers.ExecuteUpdateAsync(s => s.SetProperty(c => c.RemindersOptInAt, (DateTime?)null));
        Assert.Equal(0, await Run());

        await using (var db = api.CreateDb())
            await db.Customers.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.RemindersOptInAt, DateTime.Now)
                .SetProperty(c => c.OptedOutAt, DateTime.Now));
        Assert.Equal(0, await Run());
        Assert.Empty(api.WhatsApp.Sent);
    }

    private async Task<int> Run()
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Reminders>().SendDueAsync();
    }
}
