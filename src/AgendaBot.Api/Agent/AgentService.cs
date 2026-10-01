using System.Collections.Concurrent;
using System.Globalization;
using AgendaBot.Api.Data;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Agent;

public class AgentOptions
{
    public string Model { get; set; } = "claude-haiku-4-5";
    public int HistoryMessages { get; set; } = 20;
    public int MaxToolIterations { get; set; } = 6;
    public int MaxInputChars { get; set; } = 1000;
}

public class AgentService(
    AppDbContext db, Customers customers, Availability availability, Booking booking,
    BusinessClock clock, IOptions<BusinessOptions> business, IOptions<AgentOptions> options,
    ILogger<AgentService> log, IChatClient? llm = null)
{
    // Un turno a la vez por cliente, para que dos mensajes seguidos no pisen la propuesta pendiente.
    // Alcanza con una sola instancia de la API; con varias habría que pasarlo a un lock en la base.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    public async Task<string> HandleAsync(string phone, string text, CancellationToken ct = default)
    {
        phone = Customers.NormalizePhone(phone);
        var gate = Locks.GetOrAdd(phone, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await TurnAsync(phone, text, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> TurnAsync(string phone, string text, CancellationToken ct)
    {
        if (llm is null) return "El asistente no está configurado todavía.";
        var o = options.Value;
        text = text.Trim();
        if (text.Length > o.MaxInputChars) text = text[..o.MaxInputChars];

        var customer = await customers.GetOrCreateAsync(phone);
        var conversation = await db.Conversations.Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.CustomerId == customer.Id, ct);
        if (conversation is null)
        {
            conversation = new Conversation { CustomerId = customer.Id, Customer = customer, UpdatedAt = clock.Now };
            db.Conversations.Add(conversation);
        }

        var userMessage = new ConversationMessage { Role = "user", Text = text, CreatedAt = clock.Now };
        conversation.Messages.Add(userMessage);
        conversation.UpdatedAt = clock.Now;
        await db.SaveChangesAsync(ct);

        var history = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Id).Take(o.HistoryMessages)
            .OrderBy(m => m.Id)
            .Select(m => new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Text))
            .ToListAsync(ct);
        history.Insert(0, new ChatMessage(ChatRole.System, SystemPrompt(customer)));

        var tools = new AgentTools(db, availability, booking, clock, conversation, userMessage.Id);
        var client = new ChatClientBuilder(llm)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = o.MaxToolIterations)
            .Build();

        string reply;
        try
        {
            var response = await client.GetResponseAsync(history,
                new ChatOptions { ModelId = o.Model, Tools = tools.All() }, ct);
            reply = string.IsNullOrWhiteSpace(response.Text)
                ? "Disculpa, no te entendí bien. ¿Me lo repites?"
                : response.Text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falló el turno del agente para la conversación {ConversationId}", conversation.Id);
            reply = "Ahora mismo no puedo responder, intenta de nuevo en unos minutos.";
        }

        if (reply.Length > 4000) reply = reply[..4000];
        conversation.Messages.Add(new ConversationMessage { Role = "assistant", Text = reply, CreatedAt = clock.Now });
        await db.SaveChangesAsync(ct);
        return reply;
    }

    private string SystemPrompt(Customer customer)
    {
        var now = clock.Now;
        var name = customer.Name is null ? "todavía no sabemos su nombre" : $"se llama {customer.Name}";
        return $"""
            Eres el asistente de WhatsApp de {business.Value.Name}, una barbería en Santo Domingo.
            Ayudas a los clientes a ver servicios y precios, buscar horarios libres, agendar y cancelar citas.

            Hoy es {now.ToString("dddd d 'de' MMMM 'de' yyyy", Es)} y son las {now:HH:mm} (hora de Santo Domingo).
            El cliente que escribe {name}.

            Reglas:
            - Los servicios, precios y horarios salen siempre de las herramientas. No inventes nada.
            - Para agendar: si no sabes el nombre del cliente, pídeselo y guárdalo con set_name. Luego usa
              propose_appointment, muéstrale el resumen y pregúntale si confirma. Solo llama confirm_pending
              cuando responda que sí a esa propuesta.
            - Para cancelar, igual: propose_cancel, pregunta, y confirm_pending solo con un sí.
            - Si el cliente pide algo vago ("en la tarde", "el viernes"), conviértelo en fechas concretas y
              ofrece como mucho 3 o 4 horarios.
            - Si piden algo que no tiene que ver con la barbería, di amablemente que solo ayudas con citas.
            - Escribe como en WhatsApp: mensajes cortos, cálidos, sin markdown ni listas largas.
              Las horas en formato de 12 horas (3:30 PM).
            """;
    }
}
