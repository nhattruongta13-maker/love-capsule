using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using LoveCapsule.Api.Models;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoveCapsule.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class MemoriesController : ControllerBase
{
    private readonly MemoryService _memories;

    public MemoriesController(MemoryService memories)
    {
        _memories = memories;
    }

    [HttpGet("memories")]
    public async Task<IActionResult> GetMemories(string? search, string? mood)
    {
        var ownerUserId = GetCurrentUserId();
        return Ok(await _memories.GetOwnedMemoriesAsync(ownerUserId, search, mood));
    }

    [HttpGet("relationships/{relationshipId:int}/memories")]
    public async Task<IActionResult> GetSharedMemories(int relationshipId, string? search, string? mood)
    {
        var userId = GetCurrentUserId();
        if (!await _memories.IsAcceptedMemberAsync(userId, relationshipId) ||
            !await _memories.HasTwoAcceptedMembersAsync(relationshipId))
        {
            return Ok(new
            {
                message = "You need an accepted two-person relationship to access shared memories.",
                memories = Array.Empty<object>()
            });
        }

        return Ok(new
        {
            message = "Shared memories loaded.",
            memories = await _memories.GetSharedMemoriesAsync(userId, relationshipId, search, mood)
        });
    }

    [HttpPost("memories")]
    public async Task<IActionResult> CreateMemory(CreateMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "Title and description are required." });
        }

        var ownerUserId = GetCurrentUserId();
        var entry = await _memories.CreateAsync(request, ownerUserId);
        if (entry is null)
        {
            return BadRequest(new { message = "This memory can only be shared after both partners accept the relationship." });
        }
        return Created($"/api/memories/{entry.Id}", entry);
    }

    [HttpPut("memories/{id:int}")]
    public async Task<IActionResult> UpdateMemory(int id, CreateMemoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "Title and description are required." });
        }

        var ownerUserId = GetCurrentUserId();
        var result = await _memories.UpdateAsync(id, request, ownerUserId);
        if (result.Memory is null && result.Allowed)
        {
            return BadRequest(new { message = "This memory can only be shared after both partners accept the relationship." });
        }
        if (!result.Allowed || result.Memory is null)
        {
            return NotFound(new { message = "Memory not found." });
        }
        return Ok(result.Memory);
    }

    [HttpPost("memories/{id:int}/image")]
    public async Task<IActionResult> UploadImage(int id, IFormFile image)
    {
        const long maxFileSize = 5 * 1024 * 1024;
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();

        if (image.Length == 0 || image.Length > maxFileSize || !allowedExtensions.Contains(extension))
        {
            return BadRequest(new { message = "Use a JPG, PNG, or WebP image up to 5 MB." });
        }

        var ownerUserId = GetCurrentUserId();
        var entry = await _memories.UploadImageAsync(id, image, ownerUserId);
        if (entry is null)
        {
            return NotFound(new { message = "Memory not found." });
        }

        return Ok(entry);
    }

    [HttpDelete("memories/{id:int}")]
    public async Task<IActionResult> DeleteMemory(int id)
    {
        var ownerUserId = GetCurrentUserId();
        if (!await _memories.DeleteAsync(id, ownerUserId))
        {
            return NotFound(new { message = "Memory not found." });
        }
        return NoContent();
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
