using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoveCapsule.Api.Services;

public sealed record ChatMessage(string Role, string Content);

// ParametersSchema is a plain object graph (anonymous type or Dictionary) serialized
// as JSON Schema - Ollama/OpenAI-style tool-calling APIs expect that shape verbatim.
public sealed record ToolDefinition(string Name, string Description, object ParametersSchema);

public sealed record ToolCall(string Name, JsonElement Arguments);

public sealed record ChatResult(string? Content, IReadOnlyList<ToolCall> ToolCalls);

// Distinct from ILlmClient: this is a single non-streamed decision step over a
// message history, returning either plain text or a structured tool call -
// not the token-by-token generation ILlmClient streams for the RAG endpoint.
public interface IAgentChatClient
{
    Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken);
}

public sealed class LaptopAgentChatClient : IAgentChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public LaptopAgentChatClient(HttpClient httpClient, string model = "llama3.1:8b")
    {
        _httpClient = httpClient;
        _model = model;
    }

    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _model,
            stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            tools = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
            })
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/chat", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, cancellationToken);
        var message = body?.Message;

        var toolCalls = message?.ToolCalls?
            .Select(tc => new ToolCall(tc.Function.Name, tc.Function.Arguments))
            .ToList()
            ?? new List<ToolCall>();

        return new ChatResult(message?.Content, toolCalls);
    }

    private sealed record OllamaChatResponse([property: JsonPropertyName("message")] OllamaChatMessage? Message);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] List<OllamaToolCall>? ToolCalls);

    private sealed record OllamaToolCall(
        [property: JsonPropertyName("function")] OllamaFunctionCall Function);

    private sealed record OllamaFunctionCall(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] JsonElement Arguments);
}

// Testing-only stand-in so the suite never needs a real Ollama server on the LAN.
// Always requests the first offered tool (if any) with the user's message as the
// "query" argument, so tests can exercise the search path deterministically.
public sealed class FakeAgentChatClient : IAgentChatClient
{
    public Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        if (tools.Count == 0)
        {
            return Task.FromResult(new ChatResult("This is a fake direct answer.", Array.Empty<ToolCall>()));
        }

        var query = messages.LastOrDefault()?.Content ?? string.Empty;
        var arguments = JsonSerializer.SerializeToElement(new { query });
        var toolCall = new ToolCall(tools[0].Name, arguments);
        return Task.FromResult(new ChatResult(null, new[] { toolCall }));
    }
}
