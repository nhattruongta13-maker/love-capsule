using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoveCapsule.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class AgentController : ControllerBase
{
    private readonly MemoryService _memories;
    private readonly AgentService _agent;

    public AgentController(MemoryService memories, AgentService agent)
    {
        _memories = memories;
        _agent = agent;
    }

    // Same plain chunked streaming approach as RagController's /ask - the agent
    // additionally decides for itself whether a memory search is even needed.
    [HttpPost("relationships/{relationshipId:int}/agent-ask")]
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

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        var anyTokenSent = false;
        try
        {
            await foreach (var token in _agent.AskAsync(userId, relationshipId, request.Question, cancellationToken))
            {
                anyTokenSent = true;
                await Response.WriteAsync(token, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
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
