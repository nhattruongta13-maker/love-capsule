namespace LoveCapsule.Api.Data;

public class Relationship
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "Active";
}

public class RelationshipMember
{
    public int Id { get; set; }
    public int RelationshipId { get; set; }
    public int UserId { get; set; }
    public string Status { get; set; } = "Accepted";
    public DateTime JoinedAt { get; set; }
}

public class RelationshipInvite
{
    public int Id { get; set; }
    public int RelationshipId { get; set; }
    public int InviterUserId { get; set; }
    public int InviteeUserId { get; set; }
    public string InviteCodeHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}
