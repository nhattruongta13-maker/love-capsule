using Microsoft.EntityFrameworkCore;

namespace LoveCapsule.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<MemoryEntry> Memories => Set<MemoryEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Relationship> Relationships => Set<Relationship>();
    public DbSet<RelationshipMember> RelationshipMembers => Set<RelationshipMember>();
    public DbSet<RelationshipInvite> RelationshipInvites => Set<RelationshipInvite>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(user => user.Email)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(token => token.TokenHash)
            .IsUnique();

        modelBuilder.Entity<RelationshipInvite>()
            .HasIndex(invite => invite.InviteCodeHash)
            .IsUnique();

        modelBuilder.Entity<RelationshipMember>()
            .HasIndex(member => new { member.UserId, member.Status });

        modelBuilder.Entity<OutboxEvent>()
            .HasIndex(outboxEvent => new { outboxEvent.RecipientUserId, outboxEvent.Id });

        modelBuilder.Entity<MemoryEntry>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(memory => memory.OwnerUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class MemoryEntry
{
    public int Id { get; set; }
    public int? OwnerUserId { get; set; }
    public int? RelationshipId { get; set; }
    public MemoryVisibility Visibility { get; set; } = MemoryVisibility.Private;
    public bool PartnerCanEdit { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Mood { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
}
