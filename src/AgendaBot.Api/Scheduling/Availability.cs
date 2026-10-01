using AgendaBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Scheduling;

public record Slot(int StaffId, string StaffName, DateTime Start, DateTime End);

public record Busy(DateTime Start, DateTime End);

public class Availability(AppDbContext db, BusinessClock clock, IOptions<BusinessOptions> options)
{
    // null = el servicio no existe o está inactivo
    public async Task<List<Slot>?> GetSlotsAsync(int serviceId, DateOnly date, int? staffId = null)
    {
        var service = await db.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.IsActive);
        if (service is null) return null;

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        var staff = await db.Staff.AsNoTracking()
            .Where(s => s.IsActive && (staffId == null || s.Id == staffId))
            .Include(s => s.WorkingHours.Where(h => h.Day == date.DayOfWeek))
            .OrderBy(s => s.Name)
            .ToListAsync();
        var ids = staff.Select(s => s.Id).ToList();

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.StaffId) && a.Status != AppointmentStatus.Cancelled
                        && a.Start < dayEnd && a.End > dayStart)
            .Select(a => new { a.StaffId, a.Start, a.End })
            .ToListAsync();
        var timeOff = await db.TimeOff.AsNoTracking()
            .Where(t => ids.Contains(t.StaffId) && t.Start < dayEnd && t.End > dayStart)
            .Select(t => new { t.StaffId, t.Start, t.End })
            .ToListAsync();

        var slots = new List<Slot>();
        foreach (var s in staff)
        {
            var busy = appointments.Where(a => a.StaffId == s.Id).Select(a => new Busy(a.Start, a.End))
                .Concat(timeOff.Where(t => t.StaffId == s.Id).Select(t => new Busy(t.Start, t.End)))
                .ToList();
            foreach (var h in s.WorkingHours)
                slots.AddRange(ComputeSlots(date, h.Open, h.Close, service.DurationMinutes,
                        options.Value.SlotStepMinutes, busy, clock.Now)
                    .Select(start => new Slot(s.Id, s.Name, start, start.AddMinutes(service.DurationMinutes))));
        }

        return slots.OrderBy(x => x.Start).ThenBy(x => x.StaffName).ToList();
    }

    // Separado de la base para poder probarlo sin SQL Server.
    public static IEnumerable<DateTime> ComputeSlots(
        DateOnly date, TimeOnly open, TimeOnly close, int durationMinutes, int stepMinutes,
        IReadOnlyList<Busy> busy, DateTime now)
    {
        var start = date.ToDateTime(open);
        var limit = date.ToDateTime(close);
        for (; start.AddMinutes(durationMinutes) <= limit; start = start.AddMinutes(stepMinutes))
        {
            var end = start.AddMinutes(durationMinutes);
            if (start <= now) continue;
            if (busy.Any(b => b.Start < end && start < b.End)) continue;
            yield return start;
        }
    }
}
