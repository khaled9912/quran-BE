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

            // The browser reaches the API through the Vercel /api rewrite, so the
            // request host can be the frontend host. Prefer an explicit setting,
            // then proxy headers, then the request host.
            var configured = (config["Realtime:PublicUrl"] ?? "").Trim().TrimEnd('/');
            var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.ToString();
            var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
            var hubUrl = configured.Length > 0
                ? $"{configured}/api/realtime/hub"
                : $"{scheme}://{host}/api/realtime/hub";

            return Results.Ok(new { token, expiresAt, hub = hubUrl });
        }).RequireAuthorization();

        app.MapHub<RealtimeHub>("/api/realtime/hub");
    }
}
