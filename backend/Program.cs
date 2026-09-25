using LoveCapsule.Api.Data;
using LoveCapsule.Api.Configuration;
using LoveCapsule.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using LoveCapsule.Api.Services;
using LoveCapsule.Api.Middleware;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=/app/data/lovecapsule.db"));

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("JWT issuer is not configured.");
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("JWT audience is not configured.");
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("JWT signing key is not configured.");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();
var authRateLimitOptions = builder.Configuration
    .GetSection("Security:AuthRateLimit")
    .Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
var authPermitLimit = builder.Environment.IsEnvironment("Testing")
    ? 1000
    : authRateLimitOptions.PermitLimit;

if (authRateLimitOptions.PermitLimit <= 0 || authRateLimitOptions.PermitLimit > 1000 ||
    authRateLimitOptions.WindowSeconds <= 0 || authRateLimitOptions.WindowSeconds > 3600 ||
    authRateLimitOptions.SegmentsPerWindow <= 0 || authRateLimitOptions.SegmentsPerWindow > 60)
{
    throw new InvalidOperationException("Authentication rate-limit configuration is invalid.");
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromSeconds(authRateLimitOptions.WindowSeconds),
                SegmentsPerWindow = authRateLimitOptions.SegmentsPerWindow,
                QueueLimit = 0
            }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<RelationshipService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<AppMetrics>();
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
}
else
{
    var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("Redis connection string is not configured.");
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddSingleton<IEventBus, RedisEventBus>();
}
builder.Services.AddSingleton<MemoryEventPublisher>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseMiddleware<CorrelationAndExceptionMiddleware>();
app.Use(async (context, next) =>
{
    var metrics = context.RequestServices.GetRequiredService<AppMetrics>();
    await metrics.TrackAsync(context, next);
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("FrontendPolicy");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", message = "LoveCapsule API is live" }));
app.MapGet("/metrics", (AppMetrics metrics) => Results.Text(metrics.ToPrometheus(), "text/plain"));

app.Run();

public partial class Program { } // exposes entry point for WebApplicationFactory<Program> in tests
