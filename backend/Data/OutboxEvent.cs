namespace LoveCapsule.Api.Data;

// Durable record of a memory change, so a subscriber can recover events missed while disconnected.
public class OutboxEvent
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public int MemoryId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
