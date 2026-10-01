using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<WorkingHours> WorkingHours => Set<WorkingHours>();
    public DbSet<TimeOff> TimeOff => Set<TimeOff>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Service>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Price).HasPrecision(10, 2);
        });

        b.Entity<Staff>().Property(x => x.Name).HasMaxLength(100);

        b.Entity<WorkingHours>().HasIndex(x => new { x.StaffId, x.Day });

        b.Entity<TimeOff>().HasIndex(x => new { x.StaffId, x.Start });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Phone).IsUnique();
        });

        b.Entity<Appointment>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Este índice es el que usa la transacción serializable para bloquear solo el rango
            // de ese staff, en vez de la tabla entera.
            e.HasIndex(x => new { x.StaffId, x.Start });
            e.HasIndex(x => x.CustomerId);
            e.HasOne(x => x.Service).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Conversation>(e =>
        {
            e.HasIndex(x => x.CustomerId).IsUnique();
            e.Property(x => x.PendingKind).HasMaxLength(20);
            e.Property(x => x.PendingJson).HasMaxLength(500);
        });

        b.Entity<ConversationMessage>(e =>
        {
            e.Property(x => x.Role).HasMaxLength(10);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.HasIndex(x => new { x.ConversationId, x.Id });
        });
    }
}
