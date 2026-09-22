using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LoveCapsule.Api.Data;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoveCapsule.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly MemoryEventPublisher _publisher;
    private readonly AppDbContext _db;

    public EventsController(MemoryEventPublisher publisher, AppDbContext db)
    {
        _publisher = publisher;
        _db = db;
    }

    [HttpGet]
    public async Task Stream([FromQuery] int lastEventId, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var userId = GetCurrentUserId();
        // Subscribe before reading the backlog so nothing published in between can be missed.
        var subscription = _publisher.Subscribe(userId);
        try
        {
            await Response.WriteAsync(": connected\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);

            var highestSentId = lastEventId;
            var missedEvents = await _db.OutboxEvents
                .Where(outboxEvent => outboxEvent.RecipientUserId == userId && outboxEvent.Id > lastEventId)
                .OrderBy(outboxEvent => outboxEvent.Id)
                .ToListAsync(cancellationToken);

            foreach (var missedEvent in missedEvents)
            {
                var memoryEvent = new MemoryEvent(missedEvent.Id, missedEvent.MemoryId, missedEvent.EventType);
                var data = MemoryEventPublisher.ToSseData(memoryEvent);
                await Response.WriteAsync($"event: {memoryEvent.EventType}\ndata: {data}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                highestSentId = memoryEvent.Id;
            }

            // Ping on idle so proxies/browsers notice a dead connection instead of hanging forever.
            while (true)
            {
                var readTask = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completedTask = await Task.WhenAny(readTask, Task.Delay(HeartbeatInterval, cancellationToken));

                if (completedTask != readTask)
                {
                    await Response.WriteAsync(": ping\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                    continue;
                }

                if (!await readTask)
                {
                    break;
                }

                while (subscription.Reader.TryRead(out var memoryEvent))
                {
                    if (memoryEvent.Id <= highestSentId)
                    {
                        continue;
                    }

                    var data = MemoryEventPublisher.ToSseData(memoryEvent);
                    await Response.WriteAsync($"event: {memoryEvent.EventType}\ndata: {data}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                    highestSentId = memoryEvent.Id;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _publisher.Unsubscribe(userId, subscription.Id);
        }
    }

    private int GetCurrentUserId()
    {
        var value = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException("Authenticated user ID is missing from the token.");
    }
}
