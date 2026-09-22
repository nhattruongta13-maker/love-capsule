using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LoveCapsule.Api.Services;

public sealed class AppMetrics
{
    private long _requestsTotal;
    private long _responses5xx;
    private long _requestDurationTicks;
    private long _loginSucceeded;
    private long _loginFailed;
    private long _refreshTokenReuseDetected;
    private long _logoutCount;
    private readonly ConcurrentDictionary<MetricKey, long> _requestsByRoute = new();
    private readonly ConcurrentDictionary<MetricKey, long> _errorsByRoute = new();

    public async Task TrackAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == "/metrics")
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            Interlocked.Increment(ref _requestsTotal);
            Interlocked.Add(ref _requestDurationTicks, stopwatch.ElapsedTicks);
            if (context.Response.StatusCode >= 500)
            {
                Interlocked.Increment(ref _responses5xx);
            }

            var controllerAction = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            var route = controllerAction?.AttributeRouteInfo?.Template
                ?? context.GetEndpoint()?.DisplayName
                ?? "unmatched";
            var key = new MetricKey(context.Request.Method, route);
            _requestsByRoute.AddOrUpdate(key, 1, (_, value) => value + 1);
            if (context.Response.StatusCode >= 500)
            {
                _errorsByRoute.AddOrUpdate(key, 1, (_, value) => value + 1);
            }
        }
    }

    public string ToPrometheus()
    {
        var requests = Interlocked.Read(ref _requestsTotal);
        var errors = Interlocked.Read(ref _responses5xx);
        var durationSeconds = (double)Interlocked.Read(ref _requestDurationTicks) / Stopwatch.Frequency;
        var output = new StringBuilder();
        output.AppendLine("# HELP lovecapsule_http_requests_total Total HTTP requests.");
        output.AppendLine("# TYPE lovecapsule_http_requests_total counter");
        output.AppendLine($"lovecapsule_http_requests_total {requests.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine("# HELP lovecapsule_http_responses_5xx_total Total HTTP 5xx responses.");
        output.AppendLine("# TYPE lovecapsule_http_responses_5xx_total counter");
        output.AppendLine($"lovecapsule_http_responses_5xx_total {errors.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine("# HELP lovecapsule_http_request_duration_seconds_total Total HTTP request duration.");
        output.AppendLine("# TYPE lovecapsule_http_request_duration_seconds_total counter");
        output.AppendLine($"lovecapsule_http_request_duration_seconds_total {durationSeconds.ToString(CultureInfo.InvariantCulture)}");
        AppendCounter(output, "lovecapsule_auth_login_succeeded_total", "Successful login events.", Interlocked.Read(ref _loginSucceeded));
        AppendCounter(output, "lovecapsule_auth_login_failed_total", "Failed login events.", Interlocked.Read(ref _loginFailed));
        AppendCounter(output, "lovecapsule_auth_refresh_token_reuse_total", "Refresh-token reuse detections.", Interlocked.Read(ref _refreshTokenReuseDetected));
        AppendCounter(output, "lovecapsule_auth_logout_total", "Logout events.", Interlocked.Read(ref _logoutCount));
        output.AppendLine("# HELP lovecapsule_http_requests_by_route_total HTTP requests by method and route.");
        output.AppendLine("# TYPE lovecapsule_http_requests_by_route_total counter");
        foreach (var item in _requestsByRoute.OrderBy(item => item.Key.Route).ThenBy(item => item.Key.Method))
        {
            output.AppendLine($"lovecapsule_http_requests_by_route_total{{method=\"{Escape(item.Key.Method)}\",route=\"{Escape(item.Key.Route)}\"}} {item.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        output.AppendLine("# HELP lovecapsule_http_5xx_by_route_total HTTP 5xx responses by method and route.");
        output.AppendLine("# TYPE lovecapsule_http_5xx_by_route_total counter");
        foreach (var item in _errorsByRoute.OrderBy(item => item.Key.Route).ThenBy(item => item.Key.Method))
        {
            output.AppendLine($"lovecapsule_http_5xx_by_route_total{{method=\"{Escape(item.Key.Method)}\",route=\"{Escape(item.Key.Route)}\"}} {item.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        return output.ToString();
    }

    public void RecordAuthenticationEvent(string eventType)
    {
        switch (eventType)
        {
            case "LoginSucceeded":
                Interlocked.Increment(ref _loginSucceeded);
                break;
            case "LoginFailed":
                Interlocked.Increment(ref _loginFailed);
                break;
            case "RefreshTokenReuseDetected":
                Interlocked.Increment(ref _refreshTokenReuseDetected);
                break;
            case "Logout":
                Interlocked.Increment(ref _logoutCount);
                break;
        }
    }

    private static void AppendCounter(StringBuilder output, string name, string help, long value)
    {
        output.AppendLine($"# HELP {name} {help}");
        output.AppendLine($"# TYPE {name} counter");
        output.AppendLine($"{name} {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private readonly record struct MetricKey(string Method, string Route);
}
