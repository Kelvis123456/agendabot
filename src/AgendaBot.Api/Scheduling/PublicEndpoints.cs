using AgendaBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Scheduling;

public static class PublicEndpoints
{
    public static void MapPublic(this IEndpointRouteBuilder app)
    {
        app.MapGet("/services", (AppDbContext db) =>
            db.Services.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync());

        app.MapGet("/availability", async (int serviceId, DateOnly date, int? staffId, Availability availability) =>
            await availability.GetSlotsAsync(serviceId, date, staffId) is { } slots
                ? Results.Ok(slots)
                : Results.Problem("El servicio no existe.", statusCode: 404));
    }
}
