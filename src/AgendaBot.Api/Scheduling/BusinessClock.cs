using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Scheduling;

public class BusinessOptions
{
    public string Name { get; set; } = "AgendaBot";
    public string TimeZone { get; set; } = "America/Santo_Domingo";
    public int SlotStepMinutes { get; set; } = 30;
    // Aviso de privacidad publicado por el negocio (ver docs/privacy.md). Va en el primer mensaje.
    public string? PrivacyUrl { get; set; }
    // Teléfono o correo donde atiende una persona del negocio. También va en el primer mensaje.
    public string? Contact { get; set; }
}

// Hora "de pared" del negocio. Todo lo que se guarda y se compara usa esta hora local.
public class BusinessClock(TimeProvider time, IOptions<BusinessOptions> options)
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), _tz).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(Now);
}
