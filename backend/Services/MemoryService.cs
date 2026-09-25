using LoveCapsule.Api.Data;
using LoveCapsule.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LoveCapsule.Api.Services;

public sealed class MemoryService
{
    // Below this cosine similarity, a memory is considered semantically unrelated to the query.
    private const float SemanticSimilarityThreshold = 0.35f;

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly MemoryEventPublisher _events;
    private readonly EmbeddingService _embeddings;

    public MemoryService(AppDbContext db, IWebHostEnvironment environment, MemoryEventPublisher events, EmbeddingService embeddings)
    {
        _db = db;
        _environment = environment;
        _events = events;
        _embeddings = embeddings;
    }

    public async Task<List<object>> GetOwnedMemoriesAsync(int userId, string? search, string? mood)
    {
        var memories = await SearchAsync(_db.Memories.Where(memory => memory.OwnerUserId == userId), search, mood);
        return memories.Select(ToOwnedMemoryDto).ToList();
    }

    public async Task<List<MemoryEntry>> GetSharedMemoriesAsync(int userId, int relationshipId, string? search, string? mood)
    {
        return await SearchAsync(_db.Memories.Where(memory =>
            memory.RelationshipId == relationshipId && memory.Visibility == MemoryVisibility.Shared), search, mood);
    }

    public async Task<MemoryEntry?> CreateAsync(CreateMemoryRequest request, int ownerUserId)
    {
        var relationshipId = request.Visibility == MemoryVisibility.Shared
            ? await GetShareableRelationshipIdAsync(ownerUserId)
            : null;

        if (request.Visibility == MemoryVisibility.Shared && relationshipId is null)
        {
            return null;
        }

        var entry = new MemoryEntry
        {
            OwnerUserId = ownerUserId,
            RelationshipId = relationshipId,
            Visibility = request.Visibility,
            PartnerCanEdit = request.Visibility == MemoryVisibility.Shared && request.PartnerCanEdit,
            Title = request.Title.Trim(),
            Date = request.Date == default ? DateTime.UtcNow : request.Date,
            Mood = string.IsNullOrWhiteSpace(request.Mood) ? "Sweet" : request.Mood.Trim(),
            Description = request.Description.Trim(),
            ImageUrl = request.ImageUrl?.Trim() ?? string.Empty
        };

        // Two saves are needed because entry.Id (used by the outbox rows) isn't assigned
        // until the first SaveChangesAsync; the explicit transaction keeps them atomic.
        await using var transaction = await _db.Database.BeginTransactionAsync();
        _db.Memories.Add(entry);
        await _db.SaveChangesAsync();

        var recipients = await GetRecipientsAsync(entry);
        var outboxEvents = BuildOutboxEvents(entry.Id, "MemoryCreated", recipients);
        entry.Embedding = JsonSerializer.Serialize(_embeddings.Embed(BuildEmbeddingText(entry)));
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        await _events.PublishAsync(outboxEvents);
        return entry;
    }

    public async Task<(MemoryEntry? Memory, bool Allowed)> UpdateAsync(int id, CreateMemoryRequest request, int userId)
    {
        var entry = await _db.Memories.SingleOrDefaultAsync(memory => memory.Id == id);
        if (entry is null)
        {
            return (null, false);
        }

        var isOwner = entry.OwnerUserId == userId;
        var isPartnerEditor = !isOwner && entry.Visibility == MemoryVisibility.Shared && entry.RelationshipId is not null && entry.PartnerCanEdit &&
            await _db.RelationshipMembers.AnyAsync(member =>
                member.RelationshipId == entry.RelationshipId && member.UserId == userId && member.Status == "Accepted");
        if (!isOwner && !isPartnerEditor)
        {
            return (null, false);
        }

        if (isOwner)
        {
            var relationshipId = request.Visibility == MemoryVisibility.Shared
                ? await GetShareableRelationshipIdAsync(userId)
                : null;
            if (request.Visibility == MemoryVisibility.Shared && relationshipId is null)
            {
                return (null, true);
            }

            entry.RelationshipId = relationshipId;
            entry.Visibility = request.Visibility;
            entry.PartnerCanEdit = request.Visibility == MemoryVisibility.Shared && request.PartnerCanEdit;
        }

        entry.Title = request.Title.Trim();
        entry.Date = request.Date == default ? DateTime.UtcNow : request.Date;
        entry.Mood = string.IsNullOrWhiteSpace(request.Mood) ? "Sweet" : request.Mood.Trim();
        entry.Description = request.Description.Trim();
        entry.ImageUrl = request.ImageUrl?.Trim() ?? string.Empty;
        entry.Embedding = JsonSerializer.Serialize(_embeddings.Embed(BuildEmbeddingText(entry)));

        var recipients = await GetRecipientsAsync(entry);
        var outboxEvents = BuildOutboxEvents(entry.Id, "MemoryUpdated", recipients);
        await _db.SaveChangesAsync();

        await _events.PublishAsync(outboxEvents);
        return (entry, true);
    }

    public async Task<MemoryEntry?> UploadImageAsync(int id, IFormFile image, int ownerUserId)
    {
        var entry = await _db.Memories.SingleOrDefaultAsync(memory =>
            memory.Id == id && memory.OwnerUserId == ownerUserId);
        if (entry is null)
        {
            return null;
        }

        var uploadDirectory = Path.Combine(_environment.WebRootPath ?? "wwwroot", "uploads");
        Directory.CreateDirectory(uploadDirectory);
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var storedFileName = $"{Guid.NewGuid():N}{extension}";
        var storedFilePath = Path.Combine(uploadDirectory, storedFileName);

        await using (var fileStream = File.Create(storedFilePath))
        {
            await image.CopyToAsync(fileStream);
        }

        entry.ImageUrl = $"/uploads/{storedFileName}";

        var recipients = await GetRecipientsAsync(entry);
        var outboxEvents = BuildOutboxEvents(entry.Id, "MemoryUpdated", recipients);
        await _db.SaveChangesAsync();

        await _events.PublishAsync(outboxEvents);
        return entry;
    }

    public async Task<bool> DeleteAsync(int id, int ownerUserId)
    {
        var entry = await _db.Memories.SingleOrDefaultAsync(memory =>
            memory.Id == id && memory.OwnerUserId == ownerUserId);
        if (entry is null)
        {
            return false;
        }

        var recipients = await GetRecipientsAsync(entry);
        var outboxEvents = BuildOutboxEvents(entry.Id, "MemoryDeleted", recipients);
        _db.Memories.Remove(entry);
        await _db.SaveChangesAsync();
        await _events.PublishAsync(outboxEvents);
        return true;
    }

    public async Task<bool> IsAcceptedMemberAsync(int userId, int relationshipId)
    {
        return await _db.RelationshipMembers.AnyAsync(member =>
            member.RelationshipId == relationshipId && member.UserId == userId && member.Status == "Accepted");
    }

    public async Task<bool> HasTwoAcceptedMembersAsync(int relationshipId)
    {
        return await _db.RelationshipMembers.CountAsync(member =>
            member.RelationshipId == relationshipId && member.Status == "Accepted") == 2;
    }

    private List<OutboxEvent> BuildOutboxEvents(int memoryId, string eventType, IEnumerable<int> recipients)
    {
        var createdAt = DateTime.UtcNow;
        var outboxEvents = recipients.Select(recipientId => new OutboxEvent
        {
            RecipientUserId = recipientId,
            MemoryId = memoryId,
            EventType = eventType,
            CreatedAt = createdAt
        }).ToList();

        _db.OutboxEvents.AddRange(outboxEvents);
        return outboxEvents;
    }

    private async Task<List<int>> GetRecipientsAsync(MemoryEntry memory)
    {
        var recipients = new HashSet<int>();
        if (memory.OwnerUserId is int ownerUserId)
        {
            recipients.Add(ownerUserId);
        }

        if (memory.Visibility == MemoryVisibility.Shared && memory.RelationshipId is int relationshipId)
        {
            var partnerIds = await _db.RelationshipMembers
                .Where(member => member.RelationshipId == relationshipId && member.Status == "Accepted")
                .Select(member => member.UserId)
                .ToListAsync();
            recipients.UnionWith(partnerIds);
        }

        return recipients.ToList();
    }

    // A memory qualifies if the free-text search literally appears in it, OR if it's semantically
    // close enough to the combined search+mood query; everything that qualifies is then ranked
    // purely by semantic similarity score, so keyword matches don't automatically outrank others.
    private async Task<List<MemoryEntry>> SearchAsync(IQueryable<MemoryEntry> baseQuery, string? search, string? mood)
    {
        var memories = await baseQuery.ToListAsync();
        var queryText = string.Join(' ', new[] { search, mood }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(queryText))
        {
            return memories.OrderByDescending(memory => memory.Date).ToList();
        }

        var queryEmbedding = _embeddings.Embed(queryText);
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        var scored = new List<(MemoryEntry Memory, float Score)>();
        var backfilledAnyEmbedding = false;

        foreach (var memory in memories)
        {
            var isKeywordMatch = !string.IsNullOrWhiteSpace(normalizedSearch) && ContainsKeyword(memory, normalizedSearch);
            var embedding = GetEmbedding(memory, out var wasBackfilled);
            backfilledAnyEmbedding |= wasBackfilled;
            var score = EmbeddingService.CosineSimilarity(queryEmbedding, embedding);

            if (isKeywordMatch || score >= SemanticSimilarityThreshold)
            {
                scored.Add((memory, score));
            }
        }

        if (backfilledAnyEmbedding)
        {
            await _db.SaveChangesAsync();
        }

        return scored.OrderByDescending(entry => entry.Score).Select(entry => entry.Memory).ToList();
    }

    private static bool ContainsKeyword(MemoryEntry memory, string normalizedSearch)
    {
        return memory.Title.ToLowerInvariant().Contains(normalizedSearch)
            || memory.Description.ToLowerInvariant().Contains(normalizedSearch)
            || memory.Mood.ToLowerInvariant().Contains(normalizedSearch);
    }

    // Older rows created before this feature existed have no stored embedding yet;
    // compute and cache it here so it only needs to happen once per memory.
    private float[] GetEmbedding(MemoryEntry memory, out bool wasBackfilled)
    {
        if (!string.IsNullOrEmpty(memory.Embedding))
        {
            var cached = JsonSerializer.Deserialize<float[]>(memory.Embedding);
            if (cached is not null)
            {
                wasBackfilled = false;
                return cached;
            }
        }

        var embedding = _embeddings.Embed(BuildEmbeddingText(memory));
        memory.Embedding = JsonSerializer.Serialize(embedding);
        wasBackfilled = true;
        return embedding;
    }

    private static string BuildEmbeddingText(MemoryEntry memory) => $"{memory.Title} {memory.Description} {memory.Mood}";

    private static object ToOwnedMemoryDto(MemoryEntry memory) => new
    {
        memory.Id,
        memory.Title,
        memory.Date,
        memory.Mood,
        memory.Description,
        memory.ImageUrl,
        memory.Visibility,
        memory.PartnerCanEdit,
        shared = memory.Visibility == MemoryVisibility.Shared
    };

    private async Task<int?> GetShareableRelationshipIdAsync(int userId)
    {
        var relationshipId = await _db.RelationshipMembers
            .Where(member => member.UserId == userId && member.Status == "Accepted")
            .Select(member => (int?)member.RelationshipId)
            .SingleOrDefaultAsync();

        if (relationshipId is null || !await HasTwoAcceptedMembersAsync(relationshipId.Value))
        {
            return null;
        }

        return relationshipId;
    }
}
