using LoveCapsule.Api.Data;
using LoveCapsule.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LoveCapsule.Api.Services;

public sealed class RelationshipService
{
    private readonly AppDbContext _db;

    public RelationshipService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(Relationship? Relationship, string? Error)> CreateAsync(int userId)
    {
        if (await _db.RelationshipMembers.AnyAsync(member =>
            member.UserId == userId && member.Status == "Accepted"))
        {
            return (null, "You are already in a relationship.");
        }

        var relationship = new Relationship { CreatedAt = DateTime.UtcNow };
        _db.Relationships.Add(relationship);
        await _db.SaveChangesAsync();

        _db.RelationshipMembers.Add(new RelationshipMember
        {
            RelationshipId = relationship.Id,
            UserId = userId,
            Status = "Accepted",
            JoinedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return (relationship, null);
    }

    public async Task<object> GetAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var expiredInvites = await _db.RelationshipInvites
            .Where(invite => invite.InviteeUserId == userId && invite.Status == "Pending" && invite.ExpiresAt <= now)
            .ToListAsync();

        foreach (var invite in expiredInvites)
        {
            invite.Status = "Expired";
            invite.RespondedAt = now;
        }

        if (expiredInvites.Count > 0)
        {
            await _db.SaveChangesAsync();
        }

        var relationship = await _db.RelationshipMembers
            .Where(member => member.UserId == userId && member.Status == "Accepted")
            .Select(member => new { member.RelationshipId })
            .SingleOrDefaultAsync();

        var pendingInvites = await _db.RelationshipInvites
            .Where(invite => invite.InviteeUserId == userId && invite.Status == "Pending")
            .OrderBy(invite => invite.ExpiresAt)
            .Select(invite => new
            {
                invite.Id,
                invite.RelationshipId,
                invite.InviterUserId,
                invite.ExpiresAt,
                invite.Status
            })
            .ToListAsync();

        return new { relationship, pendingInviteCount = pendingInvites.Count, pendingInvites };
    }

    public async Task<(RelationshipInvite? Invite, string? Code, string? Error)> CreateInviteAsync(CreateInviteRequest request, int inviterId)
    {
        var membership = await _db.RelationshipMembers.SingleOrDefaultAsync(member =>
            member.UserId == inviterId && member.Status == "Accepted");
        if (membership is null)
        {
            return (null, null, "Create a relationship before sending an invitation.");
        }

        if (await _db.RelationshipMembers.CountAsync(member =>
            member.RelationshipId == membership.RelationshipId && member.Status == "Accepted") >= 2)
        {
            return (null, null, "This relationship already has two accepted members.");
        }

        if (request.InviteeUserId == inviterId)
        {
            return (null, null, "You cannot invite yourself.");
        }

        if (!await _db.Users.AnyAsync(user => user.Id == request.InviteeUserId))
        {
            return (null, null, "The invited account was not found.");
        }

        if (await _db.RelationshipMembers.AnyAsync(member =>
            member.UserId == request.InviteeUserId && member.Status == "Accepted"))
        {
            return (null, null, "The invited user is already in a relationship.");
        }

        var rawCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var invite = new RelationshipInvite
        {
            RelationshipId = membership.RelationshipId,
            InviterUserId = inviterId,
            InviteeUserId = request.InviteeUserId,
            InviteCodeHash = TokenService.HashToken(rawCode),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };

        _db.RelationshipInvites.Add(invite);
        await _db.SaveChangesAsync();
        return (invite, rawCode, null);
    }

    public async Task<(bool Success, string Message, int? RelationshipId)> AcceptInviteAsync(int id, RespondInviteRequest request, int userId)
    {
        var invite = await _db.RelationshipInvites.SingleOrDefaultAsync(candidate =>
            candidate.Id == id && candidate.InviteeUserId == userId);

        if (invite is null || invite.Status != "Pending" || invite.ExpiresAt <= DateTime.UtcNow ||
            !string.Equals(invite.InviteCodeHash, TokenService.HashToken(request.Code), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "This invitation is no longer valid.", null);
        }

        if (await _db.RelationshipMembers.AnyAsync(member =>
            member.UserId == userId && member.Status == "Accepted"))
        {
            return (false, "You are already in a relationship.", null);
        }

        invite.Status = "Accepted";
        invite.RespondedAt = DateTime.UtcNow;
        _db.RelationshipMembers.Add(new RelationshipMember
        {
            RelationshipId = invite.RelationshipId,
            UserId = userId,
            Status = "Accepted",
            JoinedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return (true, "Congratulations! You joined the relationship.", invite.RelationshipId);
    }

    public async Task<(bool Success, string Message)> DeclineInviteAsync(int id, int userId)
    {
        var invite = await _db.RelationshipInvites.SingleOrDefaultAsync(candidate =>
            candidate.Id == id && candidate.InviteeUserId == userId && candidate.Status == "Pending");
        if (invite is null)
        {
            return (false, "This invitation is no longer valid.");
        }

        invite.Status = "Declined";
        invite.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Invitation declined.");
    }

    public async Task<(bool Success, string Message)> LeaveAsync(int userId)
    {
        var membership = await _db.RelationshipMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.Status == "Accepted");
        if (membership is null)
        {
            return (false, "You are not currently in a relationship.");
        }

        var relationshipId = membership.RelationshipId;
        var members = await _db.RelationshipMembers
            .Where(member => member.RelationshipId == relationshipId && member.Status == "Accepted")
            .ToListAsync();
        foreach (var member in members)
        {
            member.Status = "Left";
        }

        var relationship = await _db.Relationships.FindAsync(relationshipId);
        if (relationship is not null)
        {
            relationship.Status = "Dissolved";
        }

        var sharedMemories = await _db.Memories
            .Where(memory => memory.RelationshipId == relationshipId && memory.Visibility == MemoryVisibility.Shared)
            .ToListAsync();
        foreach (var memory in sharedMemories)
        {
            memory.Visibility = MemoryVisibility.Private;
            memory.RelationshipId = null;
            memory.PartnerCanEdit = false;
        }

        await _db.SaveChangesAsync();
        return (true, "You left the relationship. Shared memories are private again.");
    }
}
