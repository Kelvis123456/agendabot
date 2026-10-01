using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Data;

// Barbería de ejemplo para desarrollo y para la demo pública. Solo corre si la base está vacía.
public static class DemoSeed
{
    public static async Task RunAsync(AppDbContext db)
    {
        if (await db.Services.AnyAsync()) return;

        db.Services.AddRange(
            new Service { Name = "Corte", DurationMinutes = 30, Price = 500 },
            new Service { Name = "Corte + barba", DurationMinutes = 45, Price = 750 },
            new Service { Name = "Barba", DurationMinutes = 20, Price = 300 });

        foreach (var name in new[] { "Luis", "Andrés" })
        {
            var staff = new Staff { Name = name };
            for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
                staff.WorkingHours.Add(new WorkingHours { Day = d, Open = new(9, 0), Close = new(19, 0) });
            staff.WorkingHours.Add(new WorkingHours { Day = DayOfWeek.Saturday, Open = new(9, 0), Close = new(15, 0) });
            db.Staff.Add(staff);
        }

        await db.SaveChangesAsync();
    }
}
