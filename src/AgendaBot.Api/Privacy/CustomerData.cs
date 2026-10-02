using System.Globalization;
using System.Text;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Privacy;

public enum PrivacyCommand { OptOut, OptIn, Delete }

// Lo que el cliente puede pedir sobre sus datos, por WhatsApp (palabras clave) o a través del
// dueño (endpoints del panel). Las palabras clave se resuelven acá, sin pasar por el modelo.
public class CustomerData(AppDbContext db, Customers customers, BusinessClock clock)
{
    public static PrivacyCommand? ParseCommand(string text)
    {
        // Sin tildes, sin signos y en mayúsculas: "baja.", "Borrar mis datos!" o "STOP" cuentan.
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Where(c => char.IsLetter(c) || c == ' ')
            .ToArray()).Trim().ToUpperInvariant();
        return plain switch
        {
            "BAJA" or "STOP" => PrivacyCommand.OptOut,
            "ALTA" or "START" => PrivacyCommand.OptIn,
            "BORRAR MIS DATOS" => PrivacyCommand.Delete,
            _ => null,
        };
    }

    public async Task<string> HandleAsync(string phone, PrivacyCommand command)
    {
        if (command == PrivacyCommand.Delete)
            return await DeleteAsync(phone)
                ? "Listo, borramos tu nombre, tu número y esta conversación. Si tenías citas pendientes, quedaron canceladas."
                : "No tenemos datos tuyos guardados.";

        var customer = await customers.GetOrCreateAsync(phone);
        if (command == PrivacyCommand.OptOut)
        {
            customer.OptedOutAt = clock.Now;
            await db.SaveChangesAsync();
            return "Listo, no te vamos a mandar más recordatorios. Si nos escribes, te respondemos igual. Para volver a recibirlos, escribe ALTA.";
        }

        customer.OptedOutAt = null;
        customer.RemindersOptInAt = clock.Now;
        await db.SaveChangesAsync();
        return "Listo, te vamos a recordar tus citas un día antes. Escribe BAJA cuando quieras dejar de recibirlos.";
    }

    // Borra la conversación y anonimiza al cliente. Las citas pasadas quedan, sin nombre ni número,
    // para que la agenda del negocio no tenga huecos; las futuras se cancelan.
    public async Task<bool> DeleteAsync(string phone)
    {
        phone = Customers.NormalizePhone(phone);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
        if (customer is null) return false;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Conversations.Where(c => c.CustomerId == customer.Id).ExecuteDeleteAsync();
        await db.Appointments
            .Where(a => a.CustomerId == customer.Id && a.Status == AppointmentStatus.Confirmed && a.Start > clock.Now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppointmentStatus.Cancelled));
        customer.Phone = $"borrado-{customer.Id}";
        customer.Name = null;
        customer.RemindersOptInAt = null;
        customer.OptedOutAt = clock.Now;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    // Derecho de acceso: todo lo que hay guardado de un número, para que el dueño se lo entregue.
    public async Task<object?> ExportAsync(string phone)
    {
        phone = Customers.NormalizePhone(phone);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Phone == phone);
        if (customer is null) return null;
        return new
        {
            customer.Phone,
            customer.Name,
            customer.CreatedAt,
            customer.RemindersOptInAt,
            customer.OptedOutAt,
            Appointments = await db.Appointments.AsNoTracking().Where(a => a.CustomerId == customer.Id)
                .OrderBy(a => a.Start)
                .Select(a => new { a.Start, Service = a.Service.Name, Staff = a.Staff.Name, Status = a.Status.ToString() })
                .ToListAsync(),
            Messages = await db.ConversationMessages.AsNoTracking()
                .Where(m => db.Conversations.Any(c => c.Id == m.ConversationId && c.CustomerId == customer.Id))
                .OrderBy(m => m.Id)
                .Select(m => new { m.Role, m.Text, m.CreatedAt })
                .ToListAsync(),
        };
    }

    // Para los logs: "…1234" alcanza para seguir un caso sin dejar el número completo.
    public static string Mask(string phone) => phone.Length <= 4 ? "…" : "…" + phone[^4..];
}
