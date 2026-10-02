using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgendaBot.Api.Admin;
using AgendaBot.Api.Agent;
using Anthropic;
using AgendaBot.Api.Data;
using AgendaBot.Api.Privacy;
using AgendaBot.Api.Scheduling;
using AgendaBot.Api.WhatsApp;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddProblemDetails();
// Días y estados viajan como texto ("Monday", "Confirmed"), no como números.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<BusinessOptions>(builder.Configuration.GetSection("Business"));
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddScoped<Availability>();
builder.Services.AddScoped<Booking>();
builder.Services.AddScoped<Customers>();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddScoped<AgentService>();
// El agente usa el primer proveedor con key: Gemini (tiene plan gratis) o Claude.
// Sin ninguna, la app arranca igual (panel y agenda funcionan); solo el agente queda apagado.
if (builder.Configuration["Gemini:ApiKey"] is { Length: > 0 } geminiKey)
    // SDK oficial de Google: a diferencia del endpoint compatible con OpenAI, devuelve la
    // "thought signature" de cada tool call, que Gemini 3 exige para seguir la conversación.
    // flash-lite es el que pasó las evals dentro del plan gratis (gemini-2.5-flash ya no se ofrece
    // a cuentas nuevas y gemini-3-flash-preview tiene una cuota diaria muy chica). Se cambia con Agent:Model.
    builder.Services.AddSingleton<IChatClient>(_ =>
        new Google.GenAI.Client(apiKey: geminiKey)
            .AsIChatClient(builder.Configuration["Agent:Model"] ?? "gemini-flash-lite-latest"));
else if (builder.Configuration["Anthropic:ApiKey"] is { Length: > 0 } anthropicKey)
    builder.Services.AddSingleton<IChatClient>(_ =>
        new AnthropicClient { ApiKey = anthropicKey }.AsIChatClient(
            builder.Configuration["Agent:Model"] ?? "claude-haiku-4-5", defaultMaxOutputTokens: 1024));

builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
builder.Services.AddHttpClient<WhatsAppClient>();
builder.Services.AddSingleton(Channel.CreateBounded<IncomingMessage>(1000));
builder.Services.AddSingleton<PhoneRateLimiter>();
builder.Services.AddHostedService<WhatsAppWorker>();
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminders"));
builder.Services.AddScoped<Reminders>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddScoped<CustomerData>();
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.AddScoped<Retention>();
builder.Services.AddHostedService<RetentionWorker>();

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new();
if (auth.JwtKey.Length < 32)
    throw new InvalidOperationException("Auth:JwtKey tiene que tener al menos 32 caracteres.");
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        IssuerSigningKey = Auth.SigningKey(auth),
    });
builder.Services.AddAuthorization();

var loginPerMinute = builder.Configuration.GetValue("RateLimits:LoginPerMinute", 5);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // La demo pública gasta tokens del LLM: tope por IP y tope global por día.
    var perIp = builder.Configuration.GetValue("RateLimits:DemoPerIpPer10Min", 30);
    var perDay = builder.Configuration.GetValue("RateLimits:DemoPerDay", 300);
    o.AddPolicy("demo", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perIp, Window = TimeSpan.FromMinutes(10) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/demo")
            ? RateLimitPartition.GetFixedWindowLimiter("demo-global",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perDay, Window = TimeSpan.FromDays(1) })
            : RateLimitPartition.GetNoLimiter("resto"));
});

var app = builder.Build();

// Un JSON mal formado es error del cliente (400), no del servidor.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Migrar al arrancar alcanza con una sola instancia; con varias réplicas,
// pasar a un paso de migración en el deploy.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue<bool>("Seed:Demo"))
        await DemoSeed.RunAsync(db);
}

app.MapHealthChecks("/health");
app.MapAuth();
app.MapPublic();
app.MapAdmin();
app.MapWhatsApp();
if (app.Configuration.GetValue("Demo:Enabled", true))
    app.MapDemo();

app.Run();

public partial class Program;
