using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LoveCapsule.Api.Tests;

public class RagEndpointTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;
    private readonly HttpClient _client;

    public RagEndpointTests(TestApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static async Task<int> AuthenticateClientAsync(HttpClient client)
    {
        var email = $"ragtest-{Guid.NewGuid():N}@example.com";
        const string password = "a-long-test-password";

        await client.PostAsJsonAsync("/api/auth/register", new { displayName = "Rag Tester", email, password });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return session.GetProperty("user").GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Ask_WithoutAuthentication_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/relationships/1/ask", new { question = "What did we do together?" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ask_WhenNotAcceptedMemberOfRelationship_ReturnsForbidden()
    {
        await AuthenticateClientAsync(_client);

        var response = await _client.PostAsJsonAsync("/api/relationships/999999/ask", new { question = "What did we do together?" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ask_HappyPath_StreamsGeneratedAnswer()
    {
        var ownerClient = _client;
        var ownerUserId = await AuthenticateClientAsync(ownerClient);

        var createRelationship = await ownerClient.PostAsync("/api/relationships", content: null);
        var relationship = await createRelationship.Content.ReadFromJsonAsync<JsonElement>();
        var relationshipId = relationship.GetProperty("relationshipId").GetInt32();

        using var partnerClient = _factory.CreateClient();
        var partnerUserId = await AuthenticateClientAsync(partnerClient);

        var createInvite = await ownerClient.PostAsJsonAsync("/api/relationships/invites", new { inviteeUserId = partnerUserId });
        var invite = await createInvite.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = invite.GetProperty("id").GetInt32();
        var code = invite.GetProperty("code").GetString();

        var acceptInvite = await partnerClient.PostAsJsonAsync($"/api/relationships/invites/{inviteId}/accept", new { code });
        Assert.Equal(HttpStatusCode.OK, acceptInvite.StatusCode);

        await ownerClient.PostAsJsonAsync("/api/memories", new
        {
            title = "Beach sunset",
            description = "We watched the sun go down over the water together.",
            mood = "Happy",
            date = DateTime.UtcNow,
            visibility = "Private"
        });

        var ask = await ownerClient.PostAsJsonAsync($"/api/relationships/{relationshipId}/ask", new { question = "What did we do together?" });

        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var body = await ask.Content.ReadAsStringAsync();
        Assert.Equal("This is a fake answer.", body);
    }
}
