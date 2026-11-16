using System.Net;
using System.Security.Claims;

namespace Academy.Api;

public static class RealtimeEndpoints
{
    public static string ResolvePublicHubUrl(HttpRequest request, IConfiguration config)
    {
        var configured = (config["Realtime:PublicUrl"] ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
            return $"{configured}/api/realtime/hub";

        var forwardedHost = request.Headers["X-Forwarded-Host"].ToString().Trim();
        var forwardedProto = request.Headers["X-Forwarded-Proto"].ToString();
        if (string.IsNullOrWhiteSpace(forwardedProto)) forwardedProto = request.Scheme;

        // Local browser/device testing often reaches the API through a tunnel in
        // front of the Next.js app. In Development we trust the forwarded host for
        // the short-lived SignalR ticket because the browser's socket must hit the
        // API's public URL, not the process-local loopback address.
        var isDevelopment = string.Equals(config["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);
        var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
        var isLoopbackProxy = remoteIp is not null && IPAddress.IsLoopback(remoteIp);
        if ((!string.IsNullOrWhiteSpace(forwardedHost)) && (isDevelopment || isLoopbackProxy))
        {
            var host = forwardedHost.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(host))
            {
                var forwardedPort = request.Headers["X-Forwarded-Port"].ToString().Trim();
                if (!string.IsNullOrWhiteSpace(forwardedPort) && !host.Contains(':') && !host.EndsWith($":{forwardedPort}", StringComparison.Ordinal))
                    host = $"{host}:{forwardedPort}";
                return $"{forwardedProto}://{host}/api/realtime/hub";
            }
        }

        return $"{request.Scheme}://{request.Host}/api/realtime/hub";
    }

    public static void MapRealtime(this WebApplication app)
    {
        app.MapGet("/api/realtime/ticket", (
            HttpRequest request, ClaimsPrincipal user, RealtimeTicketService tickets, IConfiguration config) =>
        {
            var (token, expiresAt) = tickets.Create(user);
            var hubUrl = ResolvePublicHubUrl(request, config);
            return Results.Ok(new { token, expiresAt, hub = hubUrl });
        }).RequireAuthorization();

        app.MapHub<RealtimeHub>("/api/realtime/hub");
    }
}
