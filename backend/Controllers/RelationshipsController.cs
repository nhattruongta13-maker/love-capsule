using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LoveCapsule.Api.Models;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoveCapsule.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/relationships")]
public class RelationshipsController : ControllerBase
{
    private readonly RelationshipService _relationships;

    public RelationshipsController(RelationshipService relationships)
    {
        _relationships = relationships;
    }

    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var result = await _relationships.CreateAsync(GetCurrentUserId());
        if (result.Relationship is null)
        {
            return Conflict(new { message = result.Error });
        }

        return Created($"/api/relationships/{result.Relationship.Id}", new
        {
            message = "Congratulations! Your relationship space was created.",
            relationshipId = result.Relationship.Id
        });
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(await _relationships.GetAsync(GetCurrentUserId()));
    }

    [HttpPost("invites")]
    public async Task<IActionResult> CreateInvite(CreateInviteRequest request)
    {
        var result = await _relationships.CreateInviteAsync(request, GetCurrentUserId());
        if (result.Invite is null)
        {
            return result.Error switch
            {
                "The invited account was not found." => NotFound(new { message = result.Error }),
                "This relationship already has two accepted members." => Conflict(new { message = result.Error }),
                "The invited user is already in a relationship." => Conflict(new { message = result.Error }),
                _ => BadRequest(new { message = result.Error })
            };
        }

        return Created($"/api/relationships/invites/{result.Invite.Id}", new
        {
            message = "Invitation created. Send this code to your partner.",
            result.Invite.Id,
            code = result.Code,
            result.Invite.ExpiresAt
        });
    }

    [HttpPost("invites/{id:int}/accept")]
    public async Task<IActionResult> AcceptInvite(int id, RespondInviteRequest request)
    {
        var result = await _relationships.AcceptInviteAsync(id, request, GetCurrentUserId());
        if (!result.Success)
        {
            return result.Message == "You are already in a relationship."
                ? Conflict(new { message = result.Message })
                : NotFound(new { message = result.Message });
        }

        return Ok(new { message = result.Message, relationshipId = result.RelationshipId });
    }

    [HttpPost("invites/{id:int}/decline")]
    public async Task<IActionResult> DeclineInvite(int id)
    {
        var result = await _relationships.DeclineInviteAsync(id, GetCurrentUserId());
        return result.Success
            ? Ok(new { message = result.Message })
            : NotFound(new { message = result.Message });
    }

    [HttpPost("leave")]
    public async Task<IActionResult> Leave()
    {
        var result = await _relationships.LeaveAsync(GetCurrentUserId());
        return result.Success
            ? Ok(new { message = result.Message })
            : NotFound(new { message = result.Message });
    }

    private int GetCurrentUserId()
    {
        var value = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException("Authenticated user ID is missing from the token.");
    }
}
