using System.Security.Claims;

namespace Academy.Api;

public static class RealtimeEndpoints
{
    public static void MapRealtime(this WebApplication app)
    {
        app.MapGet("/api/realtime/ticket", (
            HttpRequest request, ClaimsPrincipal user, RealtimeTicketService tickets, IConfiguration config) =>
        {
            var (token, expiresAt) = tickets.Create(user);

            // The ticket is requested through the frontend's /api rewrite, so the
            // hub address is only correct if the operator says what it is. Proxy
            // headers are not read here: a client controls them, and a spoofed host
            // would point the lesson room's websocket at a server of the caller's
            // choosing. When Forwarding:TrustedProxies is configured, the forwarded
            // headers middleware has already validated and applied them to Host.
            var configured = (config["Realtime:PublicUrl"] ?? "").Trim().TrimEnd('/');
            var hubUrl = configured.Length > 0
                ? $"{configured}/api/realtime/hub"
                : $"{request.Scheme}://{request.Host}/api/realtime/hub";

            return Results.Ok(new { token, expiresAt, hub = hubUrl });
        }).RequireAuthorization();

        app.MapHub<RealtimeHub>("/api/realtime/hub");
    }
}
