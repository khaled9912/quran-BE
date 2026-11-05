using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Academy.Api;

public sealed class RealtimeTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RealtimeTicketService tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = header["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(token)) return Task.FromResult(AuthenticateResult.NoResult());

        var payload = tickets.Read(token);
        if (payload is null) return Task.FromResult(AuthenticateResult.Fail("Invalid or expired realtime ticket."));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, payload.Sub) };
        claims.AddRange(payload.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
