using System.Collections.Concurrent;
using System.Globalization;
using AgendaBot.Api.Data;
using AgendaBot.Api.Privacy;
using AgendaBot.Api.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgendaBot.Api.Agent;

public class AgentOptions
{
    // null = el modelo por defecto del proveedor configurado (ver Program.cs).
    public string? Model { get; set; }
    public int HistoryMessages { get; set; } = 20;
    public int MaxToolIterations { get; set; } = 6;
    public int MaxInputChars { get; set; } = 1000;
}

// Qué herramientas usó el agente en un turno. La demo web lo muestra; WhatsApp lo ignora.
public record ToolTrace(string Name, string Arguments, bool Failed, string Result);

public record AgentReply(string Text, IReadOnlyList<ToolTrace> Tools);

public class AgentService(
    AppDbContext db, Customers customers, CustomerData customerData, Availability availability, Booking booking,
    BusinessClock clock, IOptions<BusinessOptions> business, IOptions<AgentOptions> options,
    ILogger<AgentService> log, IChatClient? llm = null)
{
    // Un turno a la vez por cliente, para que dos mensajes seguidos no pisen la propuesta pendiente.
    // Alcanza con una sola instancia de la API; con varias habría que pasarlo a un lock en la base.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    public async Task<AgentReply> HandleAsync(string phone, string text, CancellationToken ct = default)
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

    private async Task<AgentReply> TurnAsync(string phone, string text, CancellationToken ct)
    {
        // BAJA, ALTA y BORRAR MIS DATOS no pasan por el modelo ni se guardan en la conversación.
        if (CustomerData.ParseCommand(text) is { } command)
            return new AgentReply(await customerData.HandleAsync(phone, command), []);
        if (llm is null) return new AgentReply("El asistente no está configurado todavía.", []);
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
        var firstMessage = conversation.Id == 0
            || !await db.ConversationMessages.AnyAsync(m => m.ConversationId == conversation.Id && m.Role == "user", ct);

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
        history.Insert(0, new ChatMessage(ChatRole.System, SystemPrompt(customer, AgentTools.PendingSummary(conversation, clock.Now))));

        var tools = new AgentTools(db, availability, booking, clock, conversation, userMessage.Id);
        var client = new ChatClientBuilder(llm)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = o.MaxToolIterations)
            .Build();

        string reply;
        List<ToolTrace> trace = [];
        try
        {
            var response = await client.GetResponseAsync(history,
                new ChatOptions { ModelId = o.Model, Tools = tools.All() }, ct);
            trace = Trace(response.Messages);
            reply = string.IsNullOrWhiteSpace(response.Text)
                ? "Disculpa, no te entendí bien. ¿Me lo repites?"
                : response.Text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falló el turno del agente para la conversación {ConversationId}", conversation.Id);
            reply = "Ahora mismo no puedo responder, intenta de nuevo en unos minutos.";
        }

        // Que el cliente sepa desde el primer mensaje que habla con una IA, quién ve sus datos y
        // cómo darse de baja. Va en C# y no en el prompt para que no dependa del modelo.
        if (firstMessage) reply = Disclosure() + "\n\n" + reply;
        if (reply.Length > 4000) reply = reply[..4000];
        conversation.Messages.Add(new ConversationMessage { Role = "assistant", Text = reply, CreatedAt = clock.Now });
        await db.SaveChangesAsync(ct);
        return new AgentReply(reply, trace);
    }

    private string Disclosure()
    {
        var (url, contact) = (business.Value.PrivacyUrl, business.Value.Contact);
        return $"Hola, te atiende el asistente automático de {business.Value.Name}. Las respuestas las genera una IA y pueden tener errores."
               + (string.IsNullOrWhiteSpace(contact) ? " " : $" Para hablar con una persona: {contact}. ")
               + "Si agendas, te recordamos la cita por aquí un día antes; escribe BAJA si no quieres recordatorios o BORRAR MIS DATOS para eliminar tu información."
               + (string.IsNullOrWhiteSpace(url) ? "" : $" Cómo usamos tus datos: {url}");
    }

    private static List<ToolTrace> Trace(IList<ChatMessage> messages)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        var results = contents.OfType<FunctionResultContent>().ToDictionary(r => r.CallId);
        return contents.OfType<FunctionCallContent>().Select(call =>
        {
            var result = results.GetValueOrDefault(call.CallId)?.Result?.ToString() ?? "";
            var args = call.Arguments is { Count: > 0 } a ? string.Join(", ", a.Select(kv => $"{kv.Key}={kv.Value}")) : "";
            return new ToolTrace(call.Name, args, result.Contains("\"error\""), result);
        }).ToList();
    }

    private string SystemPrompt(Customer customer, string? pending)
    {
        var now = clock.Now;
        var name = customer.Name is null ? "todavía no sabemos su nombre" : $"se llama {customer.Name}";
        return $"""
            Eres el asistente de WhatsApp de {business.Value.Name}, una barbería en Santo Domingo.
            Ayudas a los clientes a ver servicios y precios, buscar horarios libres, agendar y cancelar citas.

            Hoy es {now.ToString("dddd d 'de' MMMM 'de' yyyy", Es)} y son las {now:HH:mm} (hora de Santo Domingo).
            El cliente que escribe {name}.
            {(pending is null ? "No hay ninguna propuesta pendiente." : $"Propuesta pendiente que ya le mostraste al cliente: {pending}. Si ahora la acepta, llama confirm_pending directamente, sin volver a consultar ni proponer.")}

            Reglas:
            - Los servicios, precios y horarios salen siempre de las herramientas. No inventes nada.
            - Para agendar: si no sabes el nombre del cliente, pídeselo y guárdalo con set_name. Luego usa
              propose_appointment, muéstrale el resumen y pregúntale si confirma. Solo llama confirm_pending
              cuando responda que sí a esa propuesta.
            - Para cancelar, igual: propose_cancel, pregunta, y confirm_pending solo con un sí.
            - Si una herramienta devuelve un error, no digas que lo hiciste: explícale al cliente qué pasó.
            - Si el cliente pide algo vago ("en la tarde", "el viernes"), conviértelo en fechas concretas y
              ofrece como mucho 3 o 4 horarios.
            - Si piden algo que no tiene que ver con la barbería, di amablemente que solo ayudas con citas.
            - Escribe como en WhatsApp: mensajes cortos, cálidos, sin markdown ni listas largas.
              Las horas en formato de 12 horas (3:30 PM).
            """;
    }
}
