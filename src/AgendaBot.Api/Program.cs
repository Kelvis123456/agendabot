using AgendaBot.Api.Data;
using Microsoft.EntityFrameworkCore;
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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Migrar al arrancar alcanza con una sola instancia; con varias réplicas,
// pasar a un paso de migración en el deploy.
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

app.MapHealthChecks("/health");

app.Run();

public partial class Program;
