using System.Data;
using AgendaBot.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Scheduling;

public enum BookingError { ServiceNotFound, StaffNotFound, InPast, OutsideHours, Taken }

public record BookingResult(Appointment? Appointment, BookingError? Error)
{
    public static BookingResult Ok(Appointment a) => new(a, null);
    public static BookingResult Fail(BookingError e) => new(null, e);
}

public class Booking(AppDbContext db, BusinessClock clock)
{
    public async Task<BookingResult> BookAsync(int staffId, int serviceId, int customerId, DateTime start)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.IsActive);
        if (service is null) return BookingResult.Fail(BookingError.ServiceNotFound);

        var staff = await db.Staff.Include(s => s.WorkingHours)
            .FirstOrDefaultAsync(s => s.Id == staffId && s.IsActive);
        if (staff is null) return BookingResult.Fail(BookingError.StaffNotFound);

        if (start <= clock.Now) return BookingResult.Fail(BookingError.InPast);

        var end = start.AddMinutes(service.DurationMinutes);
        var from = TimeOnly.FromDateTime(start);
        var to = TimeOnly.FromDateTime(end);
        var withinHours = end.Date == start.Date && staff.WorkingHours.Any(h =>
            h.Day == start.DayOfWeek && h.Open <= from && to <= h.Close);
        if (!withinHours) return BookingResult.Fail(BookingError.OutsideHours);

        // Serializable: el SELECT deja un bloqueo de rango sobre (StaffId, Start), así que si dos
        // personas piden el mismo horario a la vez, SQL Server deja pasar a una y a la otra la
        // elige como víctima de deadlock (1205). Eso se traduce en "horario ocupado".
        Appointment? appointment = null;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var taken = await db.Appointments.AnyAsync(a =>
                a.StaffId == staffId && a.Status != AppointmentStatus.Cancelled
                && a.Start < end && start < a.End);
            var off = await db.TimeOff.AnyAsync(t => t.StaffId == staffId && t.Start < end && start < t.End);
            if (taken || off) return BookingResult.Fail(BookingError.Taken);

            appointment = new Appointment
            {
                StaffId = staffId,
                ServiceId = serviceId,
                CustomerId = customerId,
                Start = start,
                End = end,
                CreatedAt = clock.Now,
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return BookingResult.Ok(appointment);
        }
        catch (Exception ex) when (IsDeadlock(ex))
        {
            // Solo se suelta la cita fallida; el resto del contexto (ej. la conversación del agente) sigue vivo.
            if (appointment is not null) db.Entry(appointment).State = EntityState.Detached;
            return BookingResult.Fail(BookingError.Taken);
        }
    }

    public async Task<Appointment?> CancelAsync(int appointmentId, int? customerId = null)
    {
        var a = await db.Appointments.FirstOrDefaultAsync(x =>
            x.Id == appointmentId && (customerId == null || x.CustomerId == customerId));
        if (a is null) return null;
        a.Status = AppointmentStatus.Cancelled;
        await db.SaveChangesAsync();
        return a;
    }

    private static bool IsDeadlock(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex is SqlException { Number: 1205 }) return true;
        return false;
    }
}
