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
