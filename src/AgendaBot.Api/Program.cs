using System.Threading.RateLimiting;
using AgendaBot.Api.Admin;
using AgendaBot.Api.Agent;
using Anthropic;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using AgendaBot.Api.WhatsApp;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddProblemDetails();
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
// Sin API key la app arranca igual (panel y agenda funcionan); solo el agente queda apagado.
if (builder.Configuration["Anthropic:ApiKey"] is { Length: > 0 } anthropicKey)
    builder.Services.AddSingleton<IChatClient>(_ =>
        new AnthropicClient { ApiKey = anthropicKey }.AsIChatClient(defaultMaxOutputTokens: 1024));

builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
builder.Services.AddHttpClient<WhatsAppClient>();
builder.Services.AddSingleton(Channel.CreateBounded<IncomingMessage>(1000));
builder.Services.AddSingleton<PhoneRateLimiter>();
builder.Services.AddHostedService<WhatsAppWorker>();

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
});

var app = builder.Build();

app.UseExceptionHandler();
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

app.Run();

public partial class Program;
