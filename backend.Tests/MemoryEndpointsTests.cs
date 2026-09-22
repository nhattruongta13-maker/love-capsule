using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LoveCapsule.Api.Tests;

public class MemoryEndpointsTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;
    private readonly HttpClient _client;

    public MemoryEndpointsTests(TestApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Registers a fresh test user and attaches its access token to _client for the current test.
    private async Task AuthenticateAsync()
    {
        await AuthenticateClientAsync(_client);
    }

    private static async Task<int> AuthenticateClientAsync(HttpClient client)
    {
        var email = $"memtest-{Guid.NewGuid():N}@example.com";
        const string password = "a-long-test-password";

        await client.PostAsJsonAsync("/api/auth/register", new { displayName = "Memory Tester", email, password });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return session.GetProperty("user").GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task GetMemories_ReturnsOk()
    {
        await AuthenticateAsync();
        var response = await _client.GetAsync("/api/memories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterThenLogin_ReturnsAccessToken()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var register = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            displayName = "Test User",
            email,
            password = "a-long-test-password"
        });

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "a-long-test-password"
        });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("accessToken").GetString()));
        Assert.Equal(email, session.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        var email = $"duplicate-{Guid.NewGuid():N}@example.com";
        var request = new { displayName = "Test User", email, password = "a-long-test-password" };
        var first = await _client.PostAsJsonAsync("/api/auth/register", request);
        var second = await _client.PostAsJsonAsync("/api/auth/register", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var email = $"wrong-password-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            displayName = "Test User",
            email,
            password = "a-long-test-password"
        });

        var login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "definitely-the-wrong-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task FailedLogin_ReturnsGenericUnauthorizedWithCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                email = "unknown@example.com",
                password = "wrong-password"
            })
        };
        request.Headers.Add("X-Correlation-ID", "auth-test-123");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth-test-123", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.DoesNotContain("unknown@example.com", body);
        Assert.DoesNotContain("wrong-password", body);
    }

    [Fact]
    public async Task Relationship_InviteAndAccept_WorksOnce()
    {
        using var inviterClient = _factory.CreateClient();
        using var inviteeClient = _factory.CreateClient();
        await AuthenticateClientAsync(inviterClient);
        var inviteeId = await AuthenticateClientAsync(inviteeClient);

        var relationship = await inviterClient.PostAsync("/api/relationships", null);
        Assert.Equal(HttpStatusCode.Created, relationship.StatusCode);

        var invite = await inviterClient.PostAsJsonAsync("/api/relationships/invites", new { inviteeUserId = inviteeId });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var inviteData = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = inviteData.GetProperty("id").GetInt32();
        var code = inviteData.GetProperty("code").GetString();

        var accept = await inviteeClient.PostAsJsonAsync($"/api/relationships/invites/{inviteId}/accept", new { code });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var reuse = await inviteeClient.PostAsJsonAsync($"/api/relationships/invites/{inviteId}/accept", new { code });
        Assert.Equal(HttpStatusCode.NotFound, reuse.StatusCode);
    }

    [Fact]
    public async Task Relationship_CannotBeCreatedTwiceByOneUser()
    {
        using var client = _factory.CreateClient();
        await AuthenticateClientAsync(client);

        var first = await client.PostAsync("/api/relationships", null);
        var second = await client.PostAsync("/api/relationships", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SharedMemory_IsVisibleToBothPartners_AndViewOnlyByDefault()
    {
        using var ownerClient = _factory.CreateClient();
        using var partnerClient = _factory.CreateClient();
        await AuthenticateClientAsync(ownerClient);
        var partnerId = await AuthenticateClientAsync(partnerClient);

        await ownerClient.PostAsync("/api/relationships", null);
        var invite = await ownerClient.PostAsJsonAsync("/api/relationships/invites", new { inviteeUserId = partnerId });
        var inviteData = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = inviteData.GetProperty("id").GetInt32();
        var code = inviteData.GetProperty("code").GetString();
        var accept = await partnerClient.PostAsJsonAsync($"/api/relationships/invites/{inviteId}/accept", new { code });
        accept.EnsureSuccessStatusCode();

        var create = await ownerClient.PostAsJsonAsync("/api/memories", new
        {
            title = "Shared sunset",
            date = DateTime.UtcNow,
            mood = "Loved",
            description = "A memory for both of us.",
            visibility = "Shared"
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32();
        Assert.Equal("Shared", created.GetProperty("visibility").GetString());
        Assert.False(created.GetProperty("partnerCanEdit").GetBoolean());

        var relationship = await partnerClient.GetAsync("/api/relationships");
        var relationshipData = await relationship.Content.ReadFromJsonAsync<JsonElement>();
        var relationshipId = relationshipData.GetProperty("relationship").GetProperty("relationshipId").GetInt32();
        var shared = await partnerClient.GetAsync($"/api/relationships/{relationshipId}/memories");
        var sharedData = await shared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(sharedData.GetProperty("memories").EnumerateArray(), memory =>
            memory.GetProperty("id").GetInt32() == memoryId);

        await ownerClient.DeleteAsync($"/api/memories/{memoryId}");
    }

    [Fact]
    public async Task GetEvents_ReplaysMissedEventsFromOutbox()
    {
        using var client = _factory.CreateClient();
        await AuthenticateClientAsync(client);

        // Create the memory BEFORE any stream connects, so the live channel never sees it.
        var create = await client.PostAsJsonAsync("/api/memories", new
        {
            title = "Missed while offline",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Created before the stream ever connected."
        });
        create.EnsureSuccessStatusCode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/api/events?lastEventId=0");
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);

        await using var stream = await streamResponse.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var sawMemoryCreatedEvent = false;
        string? line;
        while (!sawMemoryCreatedEvent && (line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            sawMemoryCreatedEvent = line == "event: MemoryCreated";
        }

        Assert.True(sawMemoryCreatedEvent);
    }

    [Fact]
    public async Task GetEvents_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEvents_DeliversMemoryCreatedEvent_ToOwner()
    {
        using var client = _factory.CreateClient();
        await AuthenticateClientAsync(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);

        await using var stream = await streamResponse.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var create = await client.PostAsJsonAsync("/api/memories", new
        {
            title = "Streamed memory",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Should trigger a MemoryCreated event."
        }, cts.Token);
        create.EnsureSuccessStatusCode();

        var sawMemoryCreatedEvent = false;
        string? line;
        while (!sawMemoryCreatedEvent && (line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            sawMemoryCreatedEvent = line == "event: MemoryCreated";
        }

        Assert.True(sawMemoryCreatedEvent);
    }

    [Fact]
    public async Task LeavingRelationship_MakesSharedMemoryPrivateAgain()
    {
        using var ownerClient = _factory.CreateClient();
        using var partnerClient = _factory.CreateClient();
        await AuthenticateClientAsync(ownerClient);
        var partnerId = await AuthenticateClientAsync(partnerClient);

        await ownerClient.PostAsync("/api/relationships", null);
        var invite = await ownerClient.PostAsJsonAsync("/api/relationships/invites", new { inviteeUserId = partnerId });
        var inviteData = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = inviteData.GetProperty("id").GetInt32();
        var code = inviteData.GetProperty("code").GetString();
        await partnerClient.PostAsJsonAsync($"/api/relationships/invites/{inviteId}/accept", new { code });

        var create = await ownerClient.PostAsJsonAsync("/api/memories", new
        {
            title = "Memory before leaving",
            date = DateTime.UtcNow,
            mood = "Loved",
            description = "This becomes private again.",
            visibility = "Shared"
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32();

        var leave = await partnerClient.PostAsync("/api/relationships/leave", null);
        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);

        var relationship = await ownerClient.GetAsync("/api/relationships");
        var relationshipData = await relationship.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, relationshipData.GetProperty("relationship").ValueKind);

        var ownerMemories = await ownerClient.GetAsync("/api/memories");
        var memories = await ownerMemories.Content.ReadFromJsonAsync<JsonElement[]>() ?? Array.Empty<JsonElement>();
        var memory = memories.Single(item => item.GetProperty("id").GetInt32() == memoryId);
        Assert.Equal("Private", memory.GetProperty("visibility").GetString());
        Assert.False(memory.GetProperty("shared").GetBoolean());

        await ownerClient.DeleteAsync($"/api/memories/{memoryId}");
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndRejectsReuse()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"refresh-{Guid.NewGuid():N}@example.com";
        const string password = "a-long-test-password";

        await client.PostAsJsonAsync("/api/auth/register", new { displayName = "Refresh Tester", email, password });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();

        var originalCookie = ExtractCookieValue(login, "refreshToken");
        Assert.False(string.IsNullOrWhiteSpace(originalCookie));

        using var firstRefreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        firstRefreshRequest.Headers.Add("Cookie", $"refreshToken={originalCookie}");
        var firstRefresh = await client.SendAsync(firstRefreshRequest);
        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);

        // The original cookie was rotated out by the first refresh; reusing it must be rejected.
        using var reuseRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        reuseRequest.Headers.Add("Cookie", $"refreshToken={originalCookie}");
        var reuseAttempt = await client.SendAsync(reuseRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseAttempt.StatusCode);
    }

    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            if (cookie.StartsWith($"{cookieName}="))
            {
                return cookie.Split(';')[0][(cookieName.Length + 1)..];
            }
        }

        return null;
    }

    [Fact]
    public async Task GetMemories_WithSearch_ReturnsMatchingMemory()
    {
        await AuthenticateAsync();
        var create = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Rainy day adventure",
            date = DateTime.UtcNow,
            mood = "Peaceful",
            description = "We stayed inside and watched movies."
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var response = await _client.GetAsync("/api/memories?search=rainy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memories = await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? Array.Empty<JsonElement>();
        Assert.Contains(memories, memory => memory.GetProperty("id").GetInt32() == id);

        await _client.DeleteAsync($"/api/memories/{id}");
    }

    [Fact]
    public async Task GetMemories_WithSearchAndMood_ReturnsOnlyMatchingMemory()
    {
        await AuthenticateAsync();
        var loved = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Garden walk",
            date = DateTime.UtcNow,
            mood = "Loved",
            description = "We walked through the flowers."
        });
        loved.EnsureSuccessStatusCode();
        var lovedMemory = await loved.Content.ReadFromJsonAsync<JsonElement>();
        var lovedId = lovedMemory.GetProperty("id").GetInt32();

        var happy = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Garden walk",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "We walked through the flowers."
        });
        happy.EnsureSuccessStatusCode();
        var happyMemory = await happy.Content.ReadFromJsonAsync<JsonElement>();
        var happyId = happyMemory.GetProperty("id").GetInt32();

        var response = await _client.GetAsync("/api/memories?search=garden&mood=Loved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memories = await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? Array.Empty<JsonElement>();
        Assert.Single(memories);
        Assert.Equal(lovedId, memories[0].GetProperty("id").GetInt32());

        await _client.DeleteAsync($"/api/memories/{lovedId}");
        await _client.DeleteAsync($"/api/memories/{happyId}");
    }

    [Fact]
    public async Task UserCannotReadOrModifyAnotherUsersMemory()
    {
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        await AuthenticateClientAsync(firstClient);
        await AuthenticateClientAsync(secondClient);

        var create = await firstClient.PostAsJsonAsync("/api/memories", new
        {
            title = "Private memory",
            date = DateTime.UtcNow,
            mood = "Loved",
            description = "Only the owner should see this."
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32();

        var otherUserMemories = await secondClient.GetAsync("/api/memories?search=Private%20memory");
        var memories = await otherUserMemories.Content.ReadFromJsonAsync<JsonElement[]>() ?? Array.Empty<JsonElement>();
        Assert.DoesNotContain(memories, memory => memory.GetProperty("id").GetInt32() == memoryId);

        var update = await secondClient.PutAsJsonAsync($"/api/memories/{memoryId}", new
        {
            title = "Stolen memory",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "This should not be accepted."
        });
        var delete = await secondClient.DeleteAsync($"/api/memories/{memoryId}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        await firstClient.DeleteAsync($"/api/memories/{memoryId}");
    }

    [Fact]
    public async Task PostMemory_WithoutTitle_ReturnsBadRequest()
    {
        await AuthenticateAsync();
        var response = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Missing title"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostThenDeleteMemory_ReturnsNoContent()
    {
        await AuthenticateAsync();
        var create = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Test memory",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Created during a test"
        });
        create.EnsureSuccessStatusCode();

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var delete = await _client.DeleteAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task PutMemory_UpdatesFields()
    {
        await AuthenticateAsync();
        var create = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Original title",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Original description"
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var update = await _client.PutAsJsonAsync($"/api/memories/{id}", new
        {
            title = "Updated title",
            date = DateTime.UtcNow,
            mood = "Loved",
            description = "Updated description",
            imageUrl = "https://example.com/updated-memory.jpg"
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated title", updated.GetProperty("title").GetString());
        Assert.Equal("https://example.com/updated-memory.jpg", updated.GetProperty("imageUrl").GetString());

        await _client.DeleteAsync($"/api/memories/{id}");
    }

    [Fact]
    public async Task DeleteMemory_WhenMissing_ReturnsNotFound()
    {
        await AuthenticateAsync();
        var response = await _client.DeleteAsync("/api/memories/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UploadImage_SavesImagePath()
    {
        await AuthenticateAsync();
        var create = await _client.PostAsJsonAsync("/api/memories", new
        {
            title = "Photo memory",
            date = DateTime.UtcNow,
            mood = "Happy",
            description = "Created for the upload test"
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(new byte[] { 1, 2, 3 });
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(image, "image", "memory.png");

        var upload = await _client.PostAsync($"/api/memories/{id}/image", content);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var updated = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("/uploads/", updated.GetProperty("imageUrl").GetString());

        await _client.DeleteAsync($"/api/memories/{id}");
    }
}

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    static TestApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "test-only-signing-key-that-is-not-used-in-production");
        Environment.SetEnvironmentVariable("Security__AuthRateLimit__PermitLimit", "1000");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
