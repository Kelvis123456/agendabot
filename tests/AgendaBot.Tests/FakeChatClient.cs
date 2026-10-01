using Microsoft.Extensions.AI;

namespace AgendaBot.Tests;

// Modelo de mentira: cada llamada saca la siguiente respuesta del guion. Sirve para probar lo
// que hace el código con las tool calls, no la calidad del modelo (eso va en las evals).
public class FakeChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _script = new();
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public void Reset()
    {
        _script.Clear();
        Calls.Clear();
    }

    public FakeChatClient Then(Func<IReadOnlyList<ChatMessage>, ChatResponse> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public FakeChatClient ThenText(string text) => Then(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public FakeChatClient ThenTools(params (string Name, Dictionary<string, object?> Args)[] calls) =>
        Then(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            calls.Select((c, i) => (AIContent)new FunctionCallContent($"call{Guid.NewGuid():N}{i}", c.Name, c.Args)).ToList())));

    // Resultado que recibió el modelo para una herramienta, en la última llamada.
    public string ToolResult(string toolName)
    {
        var last = Calls[^1];
        var call = last.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Last(c => c.Name == toolName);
        var result = last.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == call.CallId);
        return result.Result?.ToString() ?? "";
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var list = messages.ToList();
        Calls.Add(list);
        if (_script.Count == 0) throw new InvalidOperationException("El guion del modelo falso se quedó sin pasos.");
        return Task.FromResult(_script.Dequeue()(list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
