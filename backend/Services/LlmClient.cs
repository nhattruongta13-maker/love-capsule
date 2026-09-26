using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoveCapsule.Api.Services;

// Retrieved memories are plain text context, not tensors - the model server
// (e.g. Ollama on the laptop) owns tokenization internally over its HTTP API.
public sealed record RagRequest(string Question, IReadOnlyList<string> ContextSnippets);

// Abstracts the generation backend so callers don't care whether it's a
// laptop-hosted model or a hosted cloud API. Streams tokens as they're
// generated; can throw mid-enumeration if the backend becomes unreachable.
public interface ILlmClient
{
    IAsyncEnumerable<string> GenerateAsync(RagRequest request, CancellationToken cancellationToken);
}

// Talks to Ollama running on a laptop over the LAN. HttpClient.BaseAddress is
// configured by the caller (Program.cs) from ConnectionStrings:LlmBaseUrl.
public sealed class LaptopLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions ChunkOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public LaptopLlmClient(HttpClient httpClient, string model = "llama3.1:8b")
    {
        _httpClient = httpClient;
        _model = model;
    }

    public async IAsyncEnumerable<string> GenerateAsync(
        RagRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(new
            {
                model = _model,
                prompt = BuildPrompt(request),
                stream = true
            })
        };

        // ResponseHeadersRead is what makes this a real stream instead of buffering
        // the whole reply before we can read the first token.
        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            var chunk = JsonSerializer.Deserialize<OllamaGenerateChunk>(line, ChunkOptions);
            if (!string.IsNullOrEmpty(chunk?.Response))
            {
                yield return chunk.Response;
            }

            if (chunk?.Done == true)
            {
                yield break;
            }
        }
    }

    private static string BuildPrompt(RagRequest request)
    {
        var context = string.Join("\n\n", request.ContextSnippets);
        return $"""
            You are answering a question about a couple's shared memories. Use only the context below. If the answer isn't in the context, say you don't know.

            Context:
            {context}

            Question: {request.Question}
            """;
    }

    private sealed record OllamaGenerateChunk(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("done")] bool Done);
}

// Testing-only stand-in so the suite never needs a real Ollama server on the LAN.
public sealed class FakeLlmClient : ILlmClient
{
    public async IAsyncEnumerable<string> GenerateAsync(
        RagRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var token in new[] { "This ", "is ", "a ", "fake ", "answer." })
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return token;
            await Task.Yield();
        }
    }
}
