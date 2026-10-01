using System.ComponentModel.DataAnnotations;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Admin;

public record ServiceRequest(
    [Required, MaxLength(100)] string Name,
    [Range(5, 480)] int DurationMinutes,
    [Range(0, 1_000_000)] decimal Price);

public record StaffRequest([Required, MaxLength(100)] string Name);

public record HoursRequest(DayOfWeek Day, TimeOnly Open, TimeOnly Close);

public record TimeOffRequest(DateTime Start, DateTime End, [MaxLength(200)] string? Reason);

public record AdminBookingRequest(
    int StaffId, int ServiceId, DateTime Start,
    [Required, MaxLength(20)] string CustomerPhone,
    [MaxLength(100)] string? CustomerName);

public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization();

        admin.MapPost("/services", async (ServiceRequest req, AppDbContext db) =>
        {
            var s = new Service { Name = req.Name, DurationMinutes = req.DurationMinutes, Price = req.Price };
            db.Services.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/services/{s.Id}", s);
        });

        admin.MapPut("/services/{id:int}", async (int id, ServiceRequest req, AppDbContext db) =>
        {
            var s = await db.Services.FindAsync(id);
            if (s is null) return Results.NotFound();
            (s.Name, s.DurationMinutes, s.Price) = (req.Name, req.DurationMinutes, req.Price);
            await db.SaveChangesAsync();
            return Results.Ok(s);
        });

        // Baja lógica: las citas viejas siguen apuntando al servicio.
        admin.MapDelete("/services/{id:int}", async (int id, AppDbContext db) =>
        {
            var n = await db.Services.Where(s => s.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        admin.MapGet("/staff", (AppDbContext db) =>
            db.Staff.AsNoTracking().Include(s => s.WorkingHours).OrderBy(s => s.Name).ToListAsync());

        admin.MapPost("/staff", async (StaffRequest req, AppDbContext db) =>
        {
            var s = new Staff { Name = req.Name };
            db.Staff.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/staff/{s.Id}", s);
        });

        admin.MapDelete("/staff/{id:int}", async (int id, AppDbContext db) =>
        {
            var n = await db.Staff.Where(s => s.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Reemplaza el horario semanal completo de esa persona.
        admin.MapPut("/staff/{id:int}/hours", async (int id, List<HoursRequest> hours, AppDbContext db) =>
        {
            if (!await db.Staff.AnyAsync(s => s.Id == id)) return Results.NotFound();
            if (hours.Any(h => h.Open >= h.Close))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["hours"] = ["La hora de apertura tiene que ser antes del cierre."] });

            await db.WorkingHours.Where(h => h.StaffId == id).ExecuteDeleteAsync();
            db.WorkingHours.AddRange(hours.Select(h =>
                new WorkingHours { StaffId = id, Day = h.Day, Open = h.Open, Close = h.Close }));
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/staff/{id:int}/timeoff", async (int id, TimeOffRequest req, AppDbContext db) =>
        {
            if (!await db.Staff.AnyAsync(s => s.Id == id)) return Results.NotFound();
            if (req.Start >= req.End)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["end"] = ["El fin tiene que ser después del inicio."] });

            var t = new TimeOff { StaffId = id, Start = req.Start, End = req.End, Reason = req.Reason };
            db.TimeOff.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/staff/{id}/timeoff/{t.Id}", t);
        });

        admin.MapGet("/appointments", (DateOnly date, AppDbContext db) =>
        {
            var from = date.ToDateTime(TimeOnly.MinValue);
            var to = from.AddDays(1);
            return db.Appointments.AsNoTracking()
                .Where(a => a.Start >= from && a.Start < to)
                .OrderBy(a => a.Start)
                .Select(a => new
                {
                    a.Id,
                    a.Start,
                    a.End,
                    Status = a.Status.ToString(),
                    Staff = a.Staff.Name,
                    Service = a.Service.Name,
                    Customer = a.Customer.Name,
                    a.Customer.Phone,
                })
                .ToListAsync();
        });

        admin.MapPost("/appointments", async (AdminBookingRequest req, Customers customers, Booking booking) =>
        {
            var customer = await customers.GetOrCreateAsync(req.CustomerPhone, req.CustomerName);
            var result = await booking.BookAsync(req.StaffId, req.ServiceId, customer.Id, req.Start);
            return result.ToHttp();
        });

        admin.MapPost("/appointments/{id:int}/cancel", async (int id, Booking booking) =>
            await booking.CancelAsync(id) is { } a ? Results.Ok(new { a.Id, Status = a.Status.ToString() }) : Results.NotFound());
    }

    public static IResult ToHttp(this BookingResult r) => r.Error switch
    {
        null => Results.Created($"/admin/appointments/{r.Appointment!.Id}",
            new { r.Appointment.Id, r.Appointment.Start, r.Appointment.End, r.Appointment.StaffId, r.Appointment.ServiceId }),
        BookingError.Taken => Results.Problem("Ese horario ya está ocupado.", statusCode: 409),
        BookingError.ServiceNotFound => Results.Problem("El servicio no existe.", statusCode: 404),
        BookingError.StaffNotFound => Results.Problem("Esa persona no existe.", statusCode: 404),
        BookingError.InPast => Results.Problem("No se puede agendar en el pasado.", statusCode: 400),
        BookingError.OutsideHours => Results.Problem("Ese horario está fuera del horario de trabajo.", statusCode: 400),
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };
}
