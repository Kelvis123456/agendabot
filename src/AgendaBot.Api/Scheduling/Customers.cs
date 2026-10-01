using AgendaBot.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgendaBot.Api.Scheduling;

public class Customers(AppDbContext db, BusinessClock clock)
{
    public static string NormalizePhone(string phone) => new(phone.Where(char.IsDigit).ToArray());

    public async Task<Customer> GetOrCreateAsync(string phone, string? name = null)
    {
        phone = NormalizePhone(phone);
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
        if (existing is not null)
        {
            if (name is not null && existing.Name != name)
            {
                existing.Name = name;
                await db.SaveChangesAsync();
            }
            return existing;
        }

        var customer = new Customer { Phone = phone, Name = name, CreatedAt = clock.Now };
        db.Customers.Add(customer);
        try
        {
            await db.SaveChangesAsync();
            return customer;
        }
        // Dos mensajes del mismo número que llegan juntos: el índice único gana, releemos.
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.Entry(customer).State = EntityState.Detached;
            return await db.Customers.FirstAsync(c => c.Phone == phone);
        }
    }
}
