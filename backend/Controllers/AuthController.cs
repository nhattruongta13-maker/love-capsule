using LoveCapsule.Api.Data;
using LoveCapsule.Api.Models;
using LoveCapsule.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace LoveCapsule.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;

    public AuthController(AppDbContext db, TokenService tokens, AuditService audit)
    {
        _db = db;
        _tokens = tokens;
        _audit = audit;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(email) || request.Password.Length < 12)
        {
            return BadRequest(new { message = "Display name, email, and a password of at least 12 characters are required." });
        }

        if (await _db.Users.AnyAsync(user => user.Email == email))
        {
            return Conflict(new { message = "An account with this email already exists." });
        }

        var user = new User
        {
            DisplayName = request.DisplayName.Trim(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.Memories.AddRange(
            new MemoryEntry
            {
                OwnerUserId = user.Id,
                Title = "First coffee date",
                Date = new DateTime(2024, 6, 15),
                Mood = "Happy",
                Description = "A placeholder showing where your first shared memory can go."
            },
            new MemoryEntry
            {
                OwnerUserId = user.Id,
                Title = "Sunset walk",
                Date = new DateTime(2024, 8, 11),
                Mood = "Loved",
                Description = "A placeholder showing how a saved memory will appear in your timeline."
            });
        await _db.SaveChangesAsync();

        return Created($"/api/users/{user.Id}", new { user.Id, user.DisplayName, user.Email });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.SingleOrDefaultAsync(candidate => candidate.Email == email);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _audit.AuthenticationEvent("LoginFailed", CorrelationId(), success: false);
            return Unauthorized();
        }

        await _tokens.IssueRefreshTokenAsync(HttpContext, user, replacedTokenId: null);
        _audit.AuthenticationEvent("LoginSucceeded", CorrelationId(), user.Id, success: true);
        return Ok(new
        {
            accessToken = _tokens.CreateAccessToken(user),
            expiresAt = DateTime.UtcNow.AddMinutes(TokenService.AccessTokenLifetimeMinutes),
            user = new { user.Id, user.DisplayName, user.Email }
        });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue(TokenService.RefreshCookieName, out var rawToken) || string.IsNullOrWhiteSpace(rawToken))
        {
            _audit.AuthenticationEvent("RefreshFailed", CorrelationId(), success: false);
            return Unauthorized();
        }

        var storedToken = await _db.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == TokenService.HashToken(rawToken));
        if (storedToken is null)
        {
            _audit.AuthenticationEvent("RefreshFailed", CorrelationId(), success: false);
            return Unauthorized();
        }

        if (storedToken.RevokedAt is not null)
        {
            _audit.AuthenticationEvent("RefreshTokenReuseDetected", CorrelationId(), storedToken.UserId, success: false, highSeverity: true);
            await _tokens.RevokeAllActiveTokensAsync(storedToken.UserId);
            Response.Cookies.Delete(TokenService.RefreshCookieName);
            return Unauthorized();
        }

        if (storedToken.ExpiresAt <= DateTime.UtcNow)
        {
            _audit.AuthenticationEvent("RefreshFailed", CorrelationId(), storedToken.UserId, success: false);
            Response.Cookies.Delete(TokenService.RefreshCookieName);
            return Unauthorized();
        }

        var user = await _db.Users.FindAsync(storedToken.UserId);
        if (user is null)
        {
            _audit.AuthenticationEvent("RefreshFailed", CorrelationId(), storedToken.UserId, success: false);
            return Unauthorized();
        }

        storedToken.RevokedAt = DateTime.UtcNow;
        await _tokens.IssueRefreshTokenAsync(HttpContext, user, storedToken.Id);
        _audit.AuthenticationEvent("RefreshSucceeded", CorrelationId(), user.Id, success: true);
        return Ok(new
        {
            accessToken = _tokens.CreateAccessToken(user),
            expiresAt = DateTime.UtcNow.AddMinutes(TokenService.AccessTokenLifetimeMinutes),
            user = new { user.Id, user.DisplayName, user.Email }
        });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(TokenService.RefreshCookieName, out var rawToken) && !string.IsNullOrWhiteSpace(rawToken))
        {
            var storedToken = await _db.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == TokenService.HashToken(rawToken));
            if (storedToken is not null && storedToken.RevokedAt is null)
            {
                storedToken.RevokedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        Response.Cookies.Delete(TokenService.RefreshCookieName);
        _audit.AuthenticationEvent("Logout", CorrelationId(), success: true);
        return NoContent();
    }

    private string CorrelationId()
    {
        return HttpContext.Items["X-Correlation-ID"]?.ToString() ?? "unknown";
    }
}
