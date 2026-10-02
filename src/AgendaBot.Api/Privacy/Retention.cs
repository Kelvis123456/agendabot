using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Privacy;

public class RetentionOptions
{
    // Los mensajes solo hacen falta como contexto del agente (usa los últimos 20), así que no
    // tiene sentido guardarlos para siempre. Clientes y citas quedan: son la agenda del negocio.
    public int ConversationDays { get; set; } = 90;
    // Los ids de WhatsApp sirven para descartar reintentos de Meta, que llegan en minutos u horas.
    public int ProcessedMessageDays { get; set; } = 7;
}

public class Retention(AppDbContext db, BusinessClock clock, IOptions<RetentionOptions> options)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var messages = await db.ConversationMessages
            .Where(m => m.CreatedAt < clock.Now.AddDays(-o.ConversationDays))
            .ExecuteDeleteAsync(ct);
        await db.ProcessedWhatsAppMessages
            .Where(m => m.ReceivedAt < clock.Now.AddDays(-o.ProcessedMessageDays))
            .ExecuteDeleteAsync(ct);
        return messages;
    }
}

public class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<Retention>().RunAsync(ct);
                if (n > 0) log.LogInformation("Borrados {Count} mensajes viejos", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Falló la limpieza de mensajes viejos");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
