using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Academy.Api;

/// <summary>
/// Clients never choose their groups. Membership is derived from the ticket's
/// authenticated claims, so a student cannot subscribe to another lesson.
/// </summary>
[Authorize(AuthenticationSchemes = RealtimeTicketDefaults.Scheme)]
public sealed class RealtimeHub(ILogger<RealtimeHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var raw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var userId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userId));
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Users);
        if (Context.User!.IsInRole("admin") || Context.User.IsInRole("super_admin") || Context.User.IsInRole("support"))
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Staff);

        logger.LogDebug("Realtime connection {ConnectionId} opened for {UserId}.", Context.ConnectionId, userId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Realtime connection {ConnectionId} closed.", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public string Ping() => DateTimeOffset.UtcNow.ToString("O");
}
