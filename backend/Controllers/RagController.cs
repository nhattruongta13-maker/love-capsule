using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoveCapsule.Api.Controllers;

public sealed record AskRequest(string Question);

[ApiController]
[Authorize]
[Route("api")]
public class RagController : ControllerBase
{
    private const int RelevantMemoryCount = 5;

    private readonly MemoryService _memories;
    private readonly ILlmClient _llm;

    public RagController(MemoryService memories, ILlmClient llm)
    {
        _memories = memories;
        _llm = llm;
    }

    // Plain chunked text, not SSE: this is a one-shot question/answer, not a
    // long-lived subscription, so there's no reconnect/replay to support.
    [HttpPost("relationships/{relationshipId:int}/ask")]
    public async Task Ask(int relationshipId, [FromBody] AskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var userId = GetCurrentUserId();
        if (!await _memories.IsAcceptedMemberAsync(userId, relationshipId) ||
            !await _memories.HasTwoAcceptedMembersAsync(relationshipId))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var relevantMemories = await _memories.GetRelevantMemoriesAsync(
            userId, relationshipId, request.Question, RelevantMemoryCount);
        var contextSnippets = relevantMemories
            .Select(memory => $"{memory.Title}: {memory.Description}")
            .ToList();
        var ragRequest = new RagRequest(request.Question, contextSnippets);

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        var anyTokenSent = false;
        try
        {
            await foreach (var token in _llm.GenerateAsync(ragRequest, cancellationToken))
            {
                anyTokenSent = true;
                await Response.WriteAsync(token, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Headers are already committed once the first token is flushed, so a failure
            // that happens mid-stream can only be reported in-band, not via a status code.
            if (!anyTokenSent)
            {
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await Response.WriteAsync("Sorry, the assistant is offline right now. Please try again later.", cancellationToken);
            }
            else
            {
                await Response.WriteAsync("\n\n[Connection to the assistant was lost. Please try again.]", cancellationToken);
            }
        }
    }

    private int GetCurrentUserId()
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(userId, out var parsedUserId)
            ? parsedUserId
            : throw new InvalidOperationException("Authenticated user ID is missing from the token.");
    }
}
