namespace AgendaBot.Api.Data;

// Las fechas se guardan en hora local del negocio (America/Santo_Domingo, sin horario de verano),
// así que DateTime sin zona alcanza mientras haya un solo negocio.

public class Service
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Staff
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public List<WorkingHours> WorkingHours { get; set; } = [];
}

public class WorkingHours
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public DayOfWeek Day { get; set; }
    public TimeOnly Open { get; set; }
    public TimeOnly Close { get; set; }
}

public class TimeOff
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string? Reason { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    // E.164 sin el "+", tal como lo manda WhatsApp (ej. 18095551234)
    public required string Phone { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum AppointmentStatus { Confirmed, Cancelled, NoShow }

public class Appointment
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public int ServiceId { get; set; }
    public Service Service { get; set; } = null!;
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;
    public DateTime? ReminderSentAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Una conversación por cliente. Solo se guarda el texto que se ve en el chat; las llamadas
// a herramientas de cada turno no se persisten.
public class Conversation
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTime UpdatedAt { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];

    // Acción propuesta por el agente que espera el "sí" del cliente.
    public string? PendingKind { get; set; }
    public string? PendingJson { get; set; }
    public int? PendingAfterMessageId { get; set; }
    public DateTime? PendingAt { get; set; }
}

public class ConversationMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public required string Role { get; set; } // "user" | "assistant"
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Ids de mensajes de WhatsApp ya recibidos. Meta reintenta el webhook si no recibe 200 a tiempo,
// así que el mismo mensaje puede llegar más de una vez.
public class ProcessedWhatsAppMessage
{
    public required string Id { get; set; }
    public DateTime ReceivedAt { get; set; }
}
