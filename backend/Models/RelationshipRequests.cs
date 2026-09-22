namespace LoveCapsule.Api.Models;

public record CreateInviteRequest(int InviteeUserId);
public record RespondInviteRequest(string Code);
