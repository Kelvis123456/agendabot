using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgendaBot.Api.Agent;

// Herramientas de un turno de conversación. Las de escritura no tocan la agenda: dejan una
// propuesta pendiente, y confirm_pending solo la ejecuta en un turno posterior, o sea después
// de que el cliente haya escrito algo. El modelo no puede proponer y confirmar solo.
public class AgentTools(
    AppDbContext db, Availability availability, Booking booking, BusinessClock clock,
    Conversation conversation, int currentMessageId)
{
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(30);

    // Summary es el texto que vio el cliente; se le vuelve a mostrar al modelo en el prompt.
    private record PendingAppointment(int ServiceId, int StaffId, DateTime Start, string Summary);
    private record PendingCancel(int AppointmentId, string Summary);
    private record PendingText(string Summary);

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(ListServices, "list_services"),
        AIFunctionFactory.Create(GetAvailability, "get_availability"),
        AIFunctionFactory.Create(MyAppointments, "my_appointments"),
        AIFunctionFactory.Create(SetName, "set_name"),
        AIFunctionFactory.Create(ProposeAppointment, "propose_appointment"),
        AIFunctionFactory.Create(ProposeCancel, "propose_cancel"),
        AIFunctionFactory.Create(ConfirmPending, "confirm_pending"),
    ];

    [Description("Lista los servicios con duración en minutos y precio en pesos dominicanos.")]
    public async Task<object> ListServices() =>
        await db.Services.AsNoTracking().Where(s => s.IsActive)
            .Select(s => new { s.Id, s.Name, s.DurationMinutes, s.Price }).ToListAsync();

    [Description("Horarios libres para un servicio en una fecha. Opcionalmente filtra por barbero.")]
    public async Task<object> GetAvailability(
        int serviceId,
        [Description("Fecha en formato yyyy-MM-dd")] string date,
        int? staffId = null)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) return Error("Fecha inválida, usa yyyy-MM-dd.");
        if (d < clock.Today) return Error("Esa fecha ya pasó.");
        var slots = await availability.GetSlotsAsync(serviceId, d, staffId);
        if (slots is null) return Error("Ese servicio no existe.");
        return slots.Select(s => new { s.StaffId, s.StaffName, start = s.Start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) });
    }

    [Description("Próximas citas del cliente que está escribiendo.")]
    public async Task<object> MyAppointments() =>
        await db.Appointments.AsNoTracking()
            .Where(a => a.CustomerId == conversation.CustomerId && a.Status == AppointmentStatus.Confirmed && a.Start >= clock.Now)
            .OrderBy(a => a.Start)
            .Select(a => new { a.Id, start = a.Start, service = a.Service.Name, staff = a.Staff.Name })
            .ToListAsync();

    [Description("Guarda el nombre del cliente cuando lo dice.")]
    public async Task<object> SetName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) return Error("Nombre inválido.");
        conversation.Customer.Name = name;
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    [Description("Propone una cita. No la crea: hay que mostrarle el resumen al cliente y esperar que confirme.")]
    public async Task<object> ProposeAppointment(
        int serviceId, int staffId,
        [Description("Inicio en formato yyyy-MM-ddTHH:mm")] string start)
    {
        if (conversation.Customer.Name is null) return Error("Primero pídele el nombre al cliente.");
        if (!DateTime.TryParseExact(start, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s))
            return Error("Hora inválida, usa yyyy-MM-ddTHH:mm.");

        var slots = await availability.GetSlotsAsync(serviceId, DateOnly.FromDateTime(s), staffId);
        var slot = slots?.FirstOrDefault(x => x.Start == s);
        if (slot is null) return Error("Ese horario no está disponible. Consulta get_availability y ofrece otro.");

        var service = await db.Services.AsNoTracking().FirstAsync(x => x.Id == serviceId);
        var summary = $"{service.Name} con {slot.StaffName}, {s.ToString("dddd d 'de' MMMM, h:mm tt", Es)}, RD${service.Price:N0}";
        SetPending("appointment", new PendingAppointment(serviceId, staffId, s, summary));
        return new { pendiente = true, resumen = summary, siguiente_paso = "Pregúntale al cliente si confirma." };
    }

    [Description("Propone cancelar una cita del cliente. No la cancela: hay que esperar que confirme.")]
    public async Task<object> ProposeCancel(int appointmentId)
    {
        var a = await db.Appointments.AsNoTracking().Include(x => x.Service)
            .FirstOrDefaultAsync(x => x.Id == appointmentId && x.CustomerId == conversation.CustomerId
                                      && x.Status == AppointmentStatus.Confirmed);
        if (a is null) return Error("Esa cita no existe o no es de este cliente.");
        var summary = $"Cancelar {a.Service.Name} del {a.Start.ToString("dddd d 'de' MMMM, h:mm tt", Es)}";
        SetPending("cancel", new PendingCancel(a.Id, summary));
        return new { pendiente = true, resumen = summary, siguiente_paso = "Pregúntale al cliente si confirma." };
    }

    [Description("Ejecuta la propuesta pendiente. Llamar solo cuando el cliente respondió que sí a la última propuesta.")]
    public async Task<object> ConfirmPending()
    {
        if (conversation.PendingKind is null || clock.Now - conversation.PendingAt > PendingTtl)
            return Error("No hay nada pendiente de confirmar.");
        if (conversation.PendingAfterMessageId >= currentMessageId)
            return Error("El cliente todavía no respondió a la propuesta. Pregúntale si confirma.");

        var (kind, json) = (conversation.PendingKind, conversation.PendingJson!);
        ClearPending();
        await db.SaveChangesAsync();

        if (kind == "cancel")
        {
            var c = JsonSerializer.Deserialize<PendingCancel>(json)!;
            return await booking.CancelAsync(c.AppointmentId, conversation.CustomerId) is null
                ? Error("No se pudo cancelar.")
                : new { cancelada = true };
        }

        var p = JsonSerializer.Deserialize<PendingAppointment>(json)!;
        var result = await booking.BookAsync(p.StaffId, p.ServiceId, conversation.CustomerId, p.Start);
        // Confirmar la cita por el chat cuenta como aceptar el recordatorio (el primer mensaje ya
        // avisa que se manda), salvo que el cliente haya escrito BAJA antes.
        if (result.Error is null && conversation.Customer.OptedOutAt is null)
        {
            conversation.Customer.RemindersOptInAt ??= clock.Now;
            await db.SaveChangesAsync();
        }
        return result.Error switch
        {
            null when conversation.Customer.OptedOutAt is null => new
            {
                agendada = true,
                id = result.Appointment!.Id,
                recordatorio = "Dile que le recordaremos la cita por WhatsApp un día antes y que puede escribir BAJA si no lo quiere.",
            },
            null => new { agendada = true, id = result.Appointment!.Id },
            BookingError.Taken => Error("Alguien tomó ese horario hace un momento. Ofrece otro."),
            _ => Error($"No se pudo agendar ({result.Error})."),
        };
    }

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    // Texto de la propuesta vigente, para el prompt del turno. null si no hay o ya venció.
    public static string? PendingSummary(Conversation c, DateTime now) =>
        c.PendingJson is null || now - c.PendingAt > PendingTtl
            ? null
            : JsonSerializer.Deserialize<PendingText>(c.PendingJson)?.Summary;

    private void SetPending(string kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        // Volver a proponer exactamente lo mismo (el modelo a veces lo hace al recibir el "sí")
        // no reinicia la espera: el cliente ya vio esa propuesta en un turno anterior.
        var samePending = conversation.PendingKind == kind && conversation.PendingJson == json
                          && clock.Now - conversation.PendingAt <= PendingTtl;
        if (samePending) return;

        conversation.PendingKind = kind;
        conversation.PendingJson = json;
        conversation.PendingAfterMessageId = currentMessageId;
        conversation.PendingAt = clock.Now;
    }

    private void ClearPending()
    {
        conversation.PendingKind = null;
        conversation.PendingJson = null;
        conversation.PendingAfterMessageId = null;
        conversation.PendingAt = null;
    }

    private static object Error(string message) => new { error = message };
}
