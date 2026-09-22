namespace LoveCapsule.Api.Configuration;

public sealed class AuthRateLimitOptions
{
    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
    public int SegmentsPerWindow { get; set; } = 2;
}
