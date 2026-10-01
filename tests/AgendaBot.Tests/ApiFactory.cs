using AgendaBot.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace AgendaBot.Tests;

// SQL Server real para toda la suite. InMemory/SQLite no reproducen los bloqueos de una
// transacción serializable, que es justo lo que hay que probar.
// En CI se levanta un contenedor. En local se puede apuntar a un SQL Server ya instalado con
// AGENDABOT_TEST_SQL (ej. "Server=localhost;Database=AgendaBot_Tests;Integrated Security=True;TrustServerCertificate=True").
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string? LocalSql = Environment.GetEnvironmentVariable("AGENDABOT_TEST_SQL");

    private readonly MsSqlContainer? _sql = LocalSql is null
        ? new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()
        : null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", LocalSql ?? _sql!.GetConnectionString());
    }

    public async Task InitializeAsync()
    {
        if (_sql is not null) await _sql.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_sql is not null) await _sql.DisposeAsync();
    }

    public AppDbContext CreateDb() => Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    // Cada test arranca con las tablas vacías.
    public async Task ResetAsync()
    {
        await using var db = CreateDb();
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM Appointments; DELETE FROM TimeOff; DELETE FROM WorkingHours;
            DELETE FROM Customers; DELETE FROM Staff; DELETE FROM Services;
            """);
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
