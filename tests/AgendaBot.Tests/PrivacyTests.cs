using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgendaBot.Api.Agent;
using AgendaBot.Api.Data;
using AgendaBot.Api.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaBot.Tests;

[Collection("api")]
public class PrivacyTests(ApiFactory api) : IAsyncLifetime
{
    private const string Phone = "18095550401";

    public Task InitializeAsync() => api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("BAJA", PrivacyCommand.OptOut)]
    [InlineData(" stop. ", PrivacyCommand.OptOut)]
    [InlineData("Alta", PrivacyCommand.OptIn)]
    [InlineData("Borrar mis datos!", PrivacyCommand.Delete)]
    [InlineData("bórrar mis datos", PrivacyCommand.Delete)]
    [InlineData("quiero darme de baja de la cita", null)]
    [InlineData("cancelar", null)]
    public void Reconoce_las_palabras_clave_y_nada_mas(string text, PrivacyCommand? expected) =>
        Assert.Equal(expected, CustomerData.ParseCommand(text));

    [Fact]
    public async Task El_primer_mensaje_avisa_que_es_una_IA_y_los_siguientes_no()
    {
        api.Llm.ThenText("¿En qué te ayudo?").ThenText("Claro.");

        var first = await Say("hola");
        var second = await Say("¿cuánto cuesta un corte?");

        Assert.StartsWith("Hola, te atiende el asistente automático", first);
        Assert.Contains("Para hablar con una persona: 809-555-0100", first);
        Assert.Contains("https://ejemplo.do/privacidad", first);
        Assert.Contains("BAJA", first);
        Assert.Contains("BORRAR MIS DATOS", first);
        Assert.EndsWith("¿En qué te ayudo?", first);
        Assert.Equal("Claro.", second);
    }

    [Fact]
    public async Task BAJA_y_ALTA_no_pasan_por_el_modelo()
    {
        Assert.Contains("no te vamos a mandar más recordatorios", await Say("BAJA"));
        await using (var db = api.CreateDb())
            Assert.NotNull((await db.Customers.SingleAsync()).OptedOutAt);

        Assert.Contains("te vamos a recordar", await Say("alta"));
        await using (var db = api.CreateDb())
        {
            var c = await db.Customers.SingleAsync();
            Assert.Null(c.OptedOutAt);
            Assert.NotNull(c.RemindersOptInAt);
            Assert.Empty(db.ConversationMessages); // ni se guardan en la conversación
        }
        Assert.Empty(api.Llm.Calls);
    }

    [Fact]
    public async Task Confirmar_una_cita_por_el_chat_activa_el_recordatorio_salvo_despues_de_BAJA()
    {
        int corte, luis;
        await using (var db = api.CreateDb())
        {
            var service = new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
            var staff = new Staff { Name = "Luis", WorkingHours = [.. Enum.GetValues<DayOfWeek>().Select(d => new WorkingHours { Day = d, Open = new(0, 0), Close = new(23, 59) })] };
            db.AddRange(service, staff);
            await db.SaveChangesAsync();
            (corte, luis) = (service.Id, staff.Id);
        }
        var start = DateTime.Today.AddDays(3).AddHours(10).ToString("yyyy-MM-ddTHH:mm");
        Dictionary<string, object?> Propose(string s) => new() { ["serviceId"] = corte, ["staffId"] = luis, ["start"] = s };

        await Say("BAJA");
        api.Llm
            .ThenTools(("set_name", new() { ["name"] = "Ana" }), ("propose_appointment", Propose(start)))
            .ThenText("¿Confirmas?")
            .ThenTools(("confirm_pending", new()))
            .ThenText("Listo.");
        await Say("corte en 3 días a las 10, soy Ana");
        await Say("sí");

        await using (var db = api.CreateDb())
        {
            Assert.Equal(1, await db.Appointments.CountAsync());
            Assert.Null((await db.Customers.SingleAsync()).RemindersOptInAt);
        }

        await Say("ALTA");
        await using (var db = api.CreateDb())
            Assert.NotNull((await db.Customers.SingleAsync()).RemindersOptInAt);
    }

    [Fact]
    public async Task BORRAR_MIS_DATOS_anonimiza_al_cliente_borra_la_conversacion_y_cancela_lo_futuro()
    {
        int future, past;
        await using (var db = api.CreateDb())
        {
            var service = new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
            var staff = new Staff { Name = "Luis" };
            var customer = new Customer { Phone = Phone, Name = "Ana", CreatedAt = DateTime.Now, RemindersOptInAt = DateTime.Now };
            Appointment At(int days) => new()
            {
                Staff = staff,
                Service = service,
                Customer = customer,
                CreatedAt = DateTime.Now,
                Start = DateTime.Now.AddDays(days),
                End = DateTime.Now.AddDays(days).AddMinutes(30),
            };
            var (f, p) = (At(2), At(-2));
            db.AddRange(f, p);
            db.Conversations.Add(new Conversation
            {
                Customer = customer,
                UpdatedAt = DateTime.Now,
                Messages = [new() { Role = "user", Text = "soy Ana, mi correo es ana@x.com", CreatedAt = DateTime.Now }],
            });
            await db.SaveChangesAsync();
            (future, past) = (f.Id, p.Id);
        }

        Assert.Contains("borramos", await Say("Borrar mis datos"));

        await using (var db = api.CreateDb())
        {
            var c = await db.Customers.SingleAsync();
            Assert.Null(c.Name);
            Assert.DoesNotContain("8095550401", c.Phone);
            Assert.Empty(db.Conversations);
            Assert.Empty(db.ConversationMessages);
            Assert.Equal(AppointmentStatus.Cancelled, (await db.Appointments.FindAsync(future))!.Status);
            Assert.Equal(AppointmentStatus.Confirmed, (await db.Appointments.FindAsync(past))!.Status);
        }
        Assert.Contains("No tenemos datos", await Say("borrar mis datos"));
        Assert.Empty(api.Llm.Calls);
    }

    [Fact]
    public async Task El_panel_exporta_y_borra_los_datos_de_un_numero()
    {
        api.Llm.ThenText("¿En qué te ayudo?");
        await Say("hola");
        var http = await api.AdminClientAsync();

        var export = await http.GetFromJsonAsync<JsonElement>($"/admin/customers/{Phone}");
        Assert.Equal(Phone, export.GetProperty("phone").GetString());
        Assert.Equal(2, export.GetProperty("messages").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/admin/customers/{Phone}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/admin/customers/{Phone}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/admin/customers/{Phone}")).StatusCode);
    }

    [Fact]
    public async Task La_limpieza_borra_solo_los_mensajes_viejos()
    {
        await using (var db = api.CreateDb())
        {
            db.Conversations.Add(new Conversation
            {
                Customer = new Customer { Phone = Phone, CreatedAt = DateTime.Now },
                UpdatedAt = DateTime.Now,
                Messages =
                [
                    new() { Role = "user", Text = "viejo", CreatedAt = DateTime.Now.AddDays(-91) },
                    new() { Role = "user", Text = "nuevo", CreatedAt = DateTime.Now.AddDays(-1) },
                ],
            });
            db.ProcessedWhatsAppMessages.AddRange(
                new ProcessedWhatsAppMessage { Id = "wamid.viejo", ReceivedAt = DateTime.Now.AddDays(-8) },
                new ProcessedWhatsAppMessage { Id = "wamid.nuevo", ReceivedAt = DateTime.Now });
            await db.SaveChangesAsync();
        }

        using (var scope = api.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Retention>().RunAsync());

        await using var check = api.CreateDb();
        Assert.Equal("nuevo", (await check.ConversationMessages.SingleAsync()).Text);
        Assert.Equal("wamid.nuevo", (await check.ProcessedWhatsAppMessages.SingleAsync()).Id);
    }

    private async Task<string> Say(string text)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AgentService>().HandleAsync(Phone, text)).Text;
    }
}
