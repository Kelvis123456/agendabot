using System.Globalization;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.WhatsApp;

public class ReminderOptions
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 15;
    public int HoursBefore { get; set; } = 24;
    // Plantilla aprobada en Meta. Fuera de la ventana de 24 h solo se puede escribir con plantillas.
    // Cuerpo esperado: "Hola {{1}}, te recordamos tu cita de {{2}} {{3}}. Si no puedes venir, respóndenos y la cancelamos."
    public string Template { get; set; } = "recordatorio_cita";
    public string Language { get; set; } = "es";
}

public class Reminders(AppDbContext db, WhatsAppClient whatsapp, BusinessClock clock,
    IOptions<ReminderOptions> options, ILogger<Reminders> log)
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = clock.Now;
        var due = await db.Appointments
            .Include(a => a.Customer).Include(a => a.Service)
            .Where(a => a.Status == AppointmentStatus.Confirmed && a.ReminderSentAt == null
                        && a.Start > now && a.Start <= now.AddHours(o.HoursBefore)
                        // Si reservó hace un rato para dentro de pocas horas, no hace falta recordarle.
                        && a.CreatedAt <= now.AddHours(-2))
            .OrderBy(a => a.Start)
            .Take(100)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var a in due)
        {
            var when = a.Start.Date == now.Date.AddDays(1)
                ? $"mañana a las {a.Start.ToString("h:mm tt", Es)}"
                : a.Start.ToString("'el' dddd 'a las' h:mm tt", Es);
            try
            {
                await whatsapp.SendTemplateAsync(a.Customer.Phone, o.Template, o.Language,
                    [a.Customer.Name ?? "", a.Service.Name, when], ct);
            }
            catch (HttpRequestException ex)
            {
                // Se reintenta en la próxima vuelta.
                log.LogError(ex, "No se pudo mandar el recordatorio de la cita {AppointmentId}", a.Id);
                continue;
            }

            a.ReminderSentAt = now;
            // Para que el agente sepa de qué habla el cliente si responde "cancela".
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.CustomerId == a.CustomerId, ct);
            if (conversation is null)
            {
                conversation = new Conversation { CustomerId = a.CustomerId, UpdatedAt = now };
                db.Conversations.Add(conversation);
            }
            conversation.Messages.Add(new ConversationMessage
            {
                Role = "assistant",
                CreatedAt = now,
                Text = $"Recordatorio enviado: cita de {a.Service.Name} {when} (id {a.Id}).",
            });
            await db.SaveChangesAsync(ct);
            sent++;
        }
        return sent;
    }
}

public class ReminderWorker(IServiceScopeFactory scopes, IOptions<ReminderOptions> options, ILogger<ReminderWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<Reminders>().SendDueAsync(ct);
                if (n > 0) log.LogInformation("Enviados {Count} recordatorios", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Falló la vuelta de recordatorios");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
