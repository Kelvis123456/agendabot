using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Scheduling;

public class BusinessOptions
{
    public string Name { get; set; } = "AgendaBot";
    public string TimeZone { get; set; } = "America/Santo_Domingo";
    public int SlotStepMinutes { get; set; } = 30;
}

// Hora "de pared" del negocio. Todo lo que se guarda y se compara usa esta hora local.
public class BusinessClock(TimeProvider time, IOptions<BusinessOptions> options)
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), _tz).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
