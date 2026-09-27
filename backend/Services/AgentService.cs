using System.Runtime.CompilerServices;

namespace LoveCapsule.Api.Services;

// The "agent harness": one non-streamed decision call (call a tool, or answer
// directly), then - if a tool was requested - hand off to the already-proven
// RAG generation path for the final answer, rather than trusting a second
// tool-role chat call to synthesize it (empirically unreliable on this model).
// This linear shape also structurally caps things: at most one tool call ever
// happens per question, so there's no loop that needs a separate iteration cap.
public sealed class AgentService
{
    private static readonly ToolDefinition SearchMemoriesTool = new(
        "search_memories",
        "Search the couple's shared memories for ones relevant to a topic or question.",
        new
        {
            type = "object",
            properties = new { query = new { type = "string", description = "What to search for" } },
            required = new[] { "query" }
        });

    private const int RelevantMemoryCount = 5;

    private readonly IAgentChatClient _chat;
    private readonly ILlmClient _llm;
    private readonly MemoryService _memories;

    public AgentService(IAgentChatClient chat, ILlmClient llm, MemoryService memories)
    {
        _chat = chat;
        _llm = llm;
        _memories = memories;
    }

    public async IAsyncEnumerable<string> AskAsync(
        int userId,
        int relationshipId,
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var decision = await _chat.ChatAsync(
            new[] { new ChatMessage("user", question) },
            new[] { SearchMemoriesTool },
            cancellationToken);

        if (decision.ToolCalls.Count == 0)
        {
            yield return decision.Content ?? "I'm not sure how to answer that.";
            yield break;
        }

        var toolCall = decision.ToolCalls[0];
        var query = toolCall.Arguments.TryGetProperty("query", out var queryProp)
            ? queryProp.GetString() ?? question
            : question;

        var relevantMemories = await _memories.GetRelevantMemoriesAsync(userId, relationshipId, query, RelevantMemoryCount);
        var contextSnippets = relevantMemories.Select(memory => $"{memory.Title}: {memory.Description}").ToList();
        var ragRequest = new RagRequest(question, contextSnippets);

        await foreach (var token in _llm.GenerateAsync(ragRequest, cancellationToken))
        {
            yield return token;
        }
    }
}
