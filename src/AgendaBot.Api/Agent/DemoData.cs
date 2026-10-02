using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Agent;

// Los clientes de la demo web tienen teléfonos falsos que empiezan con 999 (ver DemoEndpoints).
// La política de privacidad promete borrarlos a los 7 días; esto es lo que lo cumple.
public class DemoData(AppDbContext db, BusinessClock clock)
{
    public const string FakePrefix = "999";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public Task<int> DeleteCustomerAsync(string phone) =>
        DeleteAsync(db.Customers.Where(c => c.Phone == phone).Select(c => c.Id));

    public Task<int> DeleteExpiredAsync()
    {
        var cutoff = clock.Now - Retention;
        // Vence si la última actividad de la conversación (o el alta, si nunca escribió) es vieja.
        var expired = db.Customers
            .Where(c => c.Phone.StartsWith(FakePrefix) && c.CreatedAt < cutoff)
            .Where(c => !db.Conversations.Any(v => v.CustomerId == c.Id && v.UpdatedAt >= cutoff))
            .Select(c => c.Id);
        return DeleteAsync(expired);
    }

    private async Task<int> DeleteAsync(IQueryable<int> customerIds)
    {
        var ids = await customerIds.ToListAsync();
        if (ids.Count == 0) return 0;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ConversationMessages.Where(m => db.Conversations.Any(v => v.Id == m.ConversationId && ids.Contains(v.CustomerId))).ExecuteDeleteAsync();
        await db.Conversations.Where(v => ids.Contains(v.CustomerId)).ExecuteDeleteAsync();
        await db.Appointments.Where(a => ids.Contains(a.CustomerId)).ExecuteDeleteAsync();
        var n = await db.Customers.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
        await tx.CommitAsync();
        return n;
    }
}

public class DemoCleanupWorker(IServiceScopeFactory scopes, ILogger<DemoCleanupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<DemoData>().DeleteExpiredAsync();
                if (n > 0) log.LogInformation("Borrados {Count} clientes de la demo con más de 7 días", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Falló la limpieza de datos de la demo");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
