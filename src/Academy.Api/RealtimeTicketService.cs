using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Academy.Api;

/// <summary>
/// Short-lived signed tickets for the hub. The browser keeps its auth cookie on
/// the frontend domain (the /api proxy), and React Native WebSockets do not use
/// the cookie jar, so neither can authenticate a direct WebSocket handshake.
/// </summary>
public sealed class RealtimeTicketService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    private readonly ITimeLimitedDataProtector _protector;

    public RealtimeTicketService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Realtime.Ticket").ToTimeLimitedDataProtector();
    }

    public (string Token, DateTimeOffset ExpiresAt) Create(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user has no name identifier.");
        var roles = user.FindAll(ClaimTypes.Role)
            .Select(x => x.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var payload = new RealtimeTicketPayload(sub, roles);
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        return (_protector.Protect(JsonSerializer.Serialize(payload), Lifetime), expiresAt);
    }

    public RealtimeTicketPayload? Read(string token)
    {
        try
        {
            var json = _protector.Unprotect(token);
            var payload = JsonSerializer.Deserialize<RealtimeTicketPayload>(json);
            return payload is null || !Guid.TryParse(payload.Sub, out _) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}
