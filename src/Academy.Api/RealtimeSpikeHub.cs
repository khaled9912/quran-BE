using Microsoft.AspNetCore.SignalR;

namespace Academy.Api;

/// TEMPORARY: transport probe for WebSockets on MonsterASP. Exposes no user
/// data and accepts anonymous connections; delete once the real-time rollout
/// replaces the polling in the web and mobile clients.
public sealed class RealtimeSpikeHub(ILogger<RealtimeSpikeHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation(
            "Realtime spike connected {ConnectionId} from {UserAgent}",
            Context.ConnectionId,
            Context.GetHttpContext()?.Request.Headers.UserAgent.ToString() ?? "unknown");

        await Clients.Caller.SendAsync("Connected", new
        {
            connectionId = Context.ConnectionId,
            serverTimeUtc = DateTimeOffset.UtcNow,
            authenticated = Context.User?.Identity?.IsAuthenticated ?? false,
        });

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Realtime spike disconnected {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public string Ping() => DateTimeOffset.UtcNow.ToString("O");
}
