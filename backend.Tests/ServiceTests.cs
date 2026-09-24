using LoveCapsule.Api.Data;
using LoveCapsule.Api.Models;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LoveCapsule.Api.Tests;

public class ServiceTests
{
    [Fact]
    public async Task MemoryEventPublisher_DeliversEventsToMultipleSubscriptions()
    {
        var publisher = new MemoryEventPublisher(new InMemoryEventBus());
        var first = publisher.Subscribe(123);
        var second = publisher.Subscribe(123);
        var outboxEvent = new OutboxEvent { Id = 7, RecipientUserId = 123, MemoryId = 42, EventType = "MemoryCreated", CreatedAt = DateTime.UtcNow };

        await publisher.PublishAsync([outboxEvent]);

        Assert.True(await first.Reader.WaitToReadAsync());
        Assert.True(await second.Reader.WaitToReadAsync());
        Assert.Equal(new MemoryEvent(7, 42, "MemoryCreated"), await first.Reader.ReadAsync());
        Assert.Equal(new MemoryEvent(7, 42, "MemoryCreated"), await second.Reader.ReadAsync());

        publisher.Unsubscribe(123, first.Id);
        publisher.Unsubscribe(123, second.Id);
    }

    [Fact]
    public async Task MemoryService_CreatesPrivateMemoryForOwner()
    {
        await using var database = new TestDatabase();
        database.Context.Users.Add(new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new MemoryService(database.Context, database.Environment, new MemoryEventPublisher(new InMemoryEventBus()));

        var memory = await service.CreateAsync(
            new CreateMemoryRequest("Private note", DateTime.UtcNow, "Happy", "Owner-only memory"),
            ownerUserId: 123);

        Assert.NotNull(memory);
        Assert.Equal(123, memory!.OwnerUserId);
        Assert.Equal(MemoryVisibility.Private, memory.Visibility);
    }

    [Fact]
    public async Task MemoryService_CreateAsync_WritesOutboxEventForOwner()
    {
        await using var database = new TestDatabase();
        database.Context.Users.Add(new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new MemoryService(database.Context, database.Environment, new MemoryEventPublisher(new InMemoryEventBus()));

        var memory = await service.CreateAsync(
            new CreateMemoryRequest("Private note", DateTime.UtcNow, "Happy", "Owner-only memory"),
            ownerUserId: 123);

        var outboxEvent = await database.Context.OutboxEvents.SingleAsync();
        Assert.Equal(memory!.Id, outboxEvent.MemoryId);
        Assert.Equal(123, outboxEvent.RecipientUserId);
        Assert.Equal("MemoryCreated", outboxEvent.EventType);
    }

    [Fact]
    public async Task MemoryService_DeleteAsync_WritesOutboxEvent_AfterMemoryIsGone()
    {
        await using var database = new TestDatabase();
        database.Context.Users.Add(new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new MemoryService(database.Context, database.Environment, new MemoryEventPublisher(new InMemoryEventBus()));

        var memory = await service.CreateAsync(
            new CreateMemoryRequest("Temporary note", DateTime.UtcNow, "Happy", "Will be deleted"),
            ownerUserId: 123);

        var deleted = await service.DeleteAsync(memory!.Id, ownerUserId: 123);

        var outboxEvents = await database.Context.OutboxEvents
            .Where(evt => evt.MemoryId == memory.Id)
            .OrderBy(evt => evt.Id)
            .ToListAsync();

        Assert.True(deleted);
        Assert.Equal(2, outboxEvents.Count);
        Assert.Equal("MemoryCreated", outboxEvents[0].EventType);
        Assert.Equal("MemoryDeleted", outboxEvents[1].EventType);
    }

    [Fact]
    public async Task MemoryService_RejectsSharedMemoryWithoutTwoPersonRelationship()
    {
        await using var database = new TestDatabase();
        database.Context.Users.Add(new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new MemoryService(database.Context, database.Environment, new MemoryEventPublisher(new InMemoryEventBus()));

        var memory = await service.CreateAsync(
            new CreateMemoryRequest("Shared note", DateTime.UtcNow, "Loved", "Needs a relationship", Visibility: MemoryVisibility.Shared),
            ownerUserId: 123);

        Assert.Null(memory);
    }

    [Fact]
    public async Task MemoryService_ReturnsOnlyTheOwnerMemories()
    {
        await using var database = new TestDatabase();
        database.Context.Users.AddRange(
            new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" },
            new User { Id = 456, Email = "456@fake.com", DisplayName = "Other User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        database.Context.Memories.AddRange(
            new MemoryEntry { OwnerUserId = 123, Title = "Mine", Description = "Owner 123", Mood = "Happy", Date = DateTime.UtcNow },
            new MemoryEntry { OwnerUserId = 456, Title = "Not mine", Description = "Owner 456", Mood = "Happy", Date = DateTime.UtcNow });
        await database.Context.SaveChangesAsync();
        var service = new MemoryService(database.Context, database.Environment, new MemoryEventPublisher(new InMemoryEventBus()));

        var memories = await service.GetOwnedMemoriesAsync(123, null, null);

        var memory = Assert.Single(memories);
        Assert.Contains("Mine", memory.ToString());
    }

    [Fact]
    public async Task RelationshipService_RejectsSecondRelationship()
    {
        await using var database = new TestDatabase();
        database.Context.Users.Add(new User { Id = 123, Email = "123@fake.com", DisplayName = "Fake User", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new RelationshipService(database.Context);

        var first = await service.CreateAsync(123);
        var second = await service.CreateAsync(123);

        Assert.NotNull(first.Relationship);
        Assert.Null(second.Relationship);
        Assert.Equal("You are already in a relationship.", second.Error);
    }

    [Fact]
    public async Task RelationshipService_AcceptsValidInvitationOnce()
    {
        await using var database = new TestDatabase();
        database.Context.Users.AddRange(
            new User { Id = 123, Email = "123@fake.com", DisplayName = "Inviter", PasswordHash = "test" },
            new User { Id = 456, Email = "456@fake.com", DisplayName = "Invitee", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new RelationshipService(database.Context);

        var relationship = await service.CreateAsync(123);
        var invite = await service.CreateInviteAsync(new CreateInviteRequest(456), 123);
        var accepted = await service.AcceptInviteAsync(invite.Invite!.Id, new RespondInviteRequest(invite.Code!), 456);
        var reused = await service.AcceptInviteAsync(invite.Invite.Id, new RespondInviteRequest(invite.Code!), 456);

        Assert.NotNull(relationship.Relationship);
        Assert.Null(invite.Error);
        Assert.True(accepted.Success);
        Assert.False(reused.Success);
        Assert.Equal("This invitation is no longer valid.", reused.Message);
    }

    [Fact]
    public async Task RelationshipService_LeavePrivatizesSharedMemories()
    {
        await using var database = new TestDatabase();
        database.Context.Users.AddRange(
            new User { Id = 123, Email = "123@fake.com", DisplayName = "Owner", PasswordHash = "test" },
            new User { Id = 456, Email = "456@fake.com", DisplayName = "Partner", PasswordHash = "test" });
        await database.Context.SaveChangesAsync();
        var service = new RelationshipService(database.Context);
        var relationship = await service.CreateAsync(123);
        var invite = await service.CreateInviteAsync(new CreateInviteRequest(456), 123);
        await service.AcceptInviteAsync(invite.Invite!.Id, new RespondInviteRequest(invite.Code!), 456);

        database.Context.Memories.Add(new MemoryEntry
        {
            OwnerUserId = 123,
            RelationshipId = relationship.Relationship!.Id,
            Visibility = MemoryVisibility.Shared,
            PartnerCanEdit = true,
            Title = "Shared",
            Description = "Shared before leaving",
            Mood = "Loved",
            Date = DateTime.UtcNow
        });
        await database.Context.SaveChangesAsync();

        var result = await service.LeaveAsync(456);
        var memory = await database.Context.Memories.SingleAsync();

        Assert.True(result.Success);
        Assert.Equal(MemoryVisibility.Private, memory.Visibility);
        Assert.Null(memory.RelationshipId);
        Assert.False(memory.PartnerCanEdit);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Context { get; }
        public IWebHostEnvironment Environment { get; } = new TestWebHostEnvironment();

        public TestDatabase()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            Context = new AppDbContext(options);
            Context.Database.EnsureCreated();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LoveCapsule.Api.Tests";
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
