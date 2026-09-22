using Microsoft.Extensions.Logging;

namespace LoveCapsule.Api.Services;

public sealed class AuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly AppMetrics _metrics;

    public AuditService(ILogger<AuditService> logger, AppMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
    }

    public void AuthenticationEvent(string eventType, string correlationId, int? userId = null, bool success = false, bool highSeverity = false)
    {
        _metrics.RecordAuthenticationEvent(eventType);
        if (highSeverity)
        {
            _logger.LogWarning(
                "Security audit event {EventType} EventId={EventId} CorrelationId={CorrelationId} UserId={UserId} Success={Success}",
                eventType, Guid.NewGuid(), correlationId, userId, success);
            return;
        }

        _logger.LogInformation(
            "Authentication audit event {EventType} EventId={EventId} CorrelationId={CorrelationId} UserId={UserId} Success={Success}",
            eventType, Guid.NewGuid(), correlationId, userId, success);
    }
}
