using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaBot.Tests;

[Collection("api")]
public class BookingTests(ApiFactory api) : IAsyncLifetime
{
    // Siempre un lunes de la semana que viene, para no depender de la hora real.
    private static readonly DateOnly Monday = NextMonday();
    private int _corte, _luis, _andres;

    public async Task InitializeAsync()
    {
        await api.ResetAsync();
        await using var db = api.CreateDb();
        var corte = new Service { Name = "Corte", DurationMinutes = 30, Price = 500 };
        var luis = new Staff { Name = "Luis", WorkingHours = [new() { Day = DayOfWeek.Monday, Open = new(9, 0), Close = new(12, 0) }] };
        var andres = new Staff { Name = "Andrés", WorkingHours = [new() { Day = DayOfWeek.Monday, Open = new(9, 0), Close = new(12, 0) }] };
        db.AddRange(corte, luis, andres);
        await db.SaveChangesAsync();
        (_corte, _luis, _andres) = (corte.Id, luis.Id, andres.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reserva_un_horario_libre_y_rechaza_el_mismo_horario_despues()
    {
        var http = await api.AdminClientAsync();
        var first = await http.PostAsJsonAsync("/admin/appointments", Request(_luis, 10));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await http.PostAsJsonAsync("/admin/appointments", Request(_luis, 10, "18095550002"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Otra persona del staff a la misma hora sí puede.
        var other = await http.PostAsJsonAsync("/admin/appointments", Request(_andres, 10, "18095550002"));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Rechaza_horarios_fuera_del_horario_de_trabajo()
    {
        var http = await api.AdminClientAsync();
        var late = await http.PostAsJsonAsync("/admin/appointments", Request(_luis, 11.75)); // 11:45 + 30 min > 12:00
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
    }

    [Fact]
    public async Task Dos_reservas_simultaneas_del_mismo_horario_solo_deja_entrar_una()
    {
        int customerId;
        await using (var db = api.CreateDb())
        {
            var c = new Customer { Phone = "18095550001", CreatedAt = DateTime.Now };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            customerId = c.Id;
        }

        var start = Monday.ToDateTime(new(10, 0));
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = api.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<Booking>()
                .BookAsync(_luis, _corte, customerId, start);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Error is null);
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal(BookingError.Taken, r.Error));
        await using var check = api.CreateDb();
        Assert.Equal(1, await check.Appointments.CountAsync());
    }

    [Fact]
    public async Task Disponibilidad_no_muestra_lo_reservado_y_cancelar_lo_libera()
    {
        var http = await api.AdminClientAsync();
        var created = await http.PostAsJsonAsync("/admin/appointments", Request(_luis, 9));
        var id = (await created.Content.ReadFromJsonAsync<IdOnly>())!.Id;

        var slots = await Slots(http);
        Assert.DoesNotContain(slots, s => s.StaffId == _luis && s.Start.Hour == 9 && s.Start.Minute == 0);
        Assert.Contains(slots, s => s.StaffId == _andres && s.Start.Hour == 9 && s.Start.Minute == 0);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/admin/appointments/{id}/cancel", null)).StatusCode);
        Assert.Contains(await Slots(http), s => s.StaffId == _luis && s.Start.Hour == 9 && s.Start.Minute == 0);
    }

    [Fact]
    public async Task El_panel_admin_pide_token_y_el_login_rechaza_una_clave_mala()
    {
        var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/admin/staff")).StatusCode);

        var login = await http.PostAsJsonAsync("/auth/login", new { password = "no-es-esta" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private object Request(int staffId, double hour, string phone = "18095550001") => new
    {
        staffId, serviceId = _corte, customerPhone = phone, customerName = "Cliente",
        start = Monday.ToDateTime(TimeOnly.MinValue).AddHours(hour),
    };

    private async Task<List<Slot>> Slots(HttpClient http) =>
        (await http.GetFromJsonAsync<List<Slot>>($"/availability?serviceId={_corte}&date={Monday:yyyy-MM-dd}"))!;

    private static DateOnly NextMonday()
    {
        var d = DateOnly.FromDateTime(DateTime.Today).AddDays(7);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d;
    }

    private record IdOnly(int Id);
}

public static class ApiFactoryExtensions
{
    public static async Task<HttpClient> AdminClientAsync(this ApiFactory api)
    {
        var http = api.CreateClient();
        var res = await http.PostAsJsonAsync("/auth/login", new { password = ApiFactory.AdminPassword });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!["token"]);
        return http;
    }
}
