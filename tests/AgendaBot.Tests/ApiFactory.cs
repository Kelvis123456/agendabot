using AgendaBot.Api.Data;
using AgendaBot.Api.WhatsApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
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

    public const string AdminPassword = "clave-de-test";
    public const string WhatsAppSecret = "secreto-de-la-app";
    public const string WhatsAppVerifyToken = "token-de-verificacion";
    public FakeChatClient Llm { get; } = new();

    // Las evals usan el modelo real (clave de configuración + valor); el resto de la suite, el falso.
    protected virtual (string Setting, string Value)? RealLlm => null;
    public FakeWhatsApp WhatsApp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", LocalSql ?? _sql!.GetConnectionString());
        builder.UseSetting("Auth:AdminPassword", AdminPassword);
        builder.UseSetting("Auth:JwtKey", "clave-jwt-de-test-con-mas-de-32-caracteres");
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");
        builder.UseSetting("WhatsApp:AppSecret", WhatsAppSecret);
        builder.UseSetting("WhatsApp:VerifyToken", WhatsAppVerifyToken);
        builder.UseSetting("WhatsApp:AccessToken", "token-de-acceso");
        builder.UseSetting("WhatsApp:PhoneNumberId", "123456");
        builder.UseSetting("Business:Contact", "809-555-0100");
        builder.UseSetting("Business:PrivacyUrl", "https://ejemplo.do/privacidad");
        if (RealLlm is { } llm) builder.UseSetting(llm.Setting, llm.Value);
        builder.ConfigureTestServices(s =>
        {
            if (RealLlm is null) s.AddSingleton<IChatClient>(Llm);
            s.AddHttpClient<WhatsAppClient>().ConfigurePrimaryHttpMessageHandler(() => WhatsApp)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan); // el fake es una sola instancia, que no lo desechen
        });
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
        Llm.Reset();
        WhatsApp.Sent.Clear();
        WhatsApp.Status = System.Net.HttpStatusCode.OK;
        await using var db = CreateDb();
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM ProcessedWhatsAppMessages; DELETE FROM ConversationMessages; DELETE FROM Conversations; DELETE FROM Appointments; DELETE FROM TimeOff; DELETE FROM WorkingHours;
            DELETE FROM Customers; DELETE FROM Staff; DELETE FROM Services;
            """);
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;

public class EvalFactory : ApiFactory
{
    public static (string Setting, string Value)? FromEnvironment() =>
        Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 } gemini ? ("Gemini:ApiKey", gemini)
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } claude ? ("Anthropic:ApiKey", claude)
        : null;

    protected override (string Setting, string Value)? RealLlm => FromEnvironment();
}

[CollectionDefinition("eval")]
public class EvalCollection : ICollectionFixture<EvalFactory>;
