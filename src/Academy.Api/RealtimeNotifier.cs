using Microsoft.AspNetCore.SignalR;

namespace Academy.Api;

/// <summary>
/// Fire-and-forget change hints. Publication must never fail a business
/// request: REST stays authoritative and clients reconcile on reconnect.
/// </summary>
public sealed class RealtimeNotifier(IHubContext<RealtimeHub> hub, ILogger<RealtimeNotifier> logger)
{
    public Task ToUserAsync(Guid userId, string scope, Guid? id = null) =>
        SendAsync(RealtimeGroups.User(userId), scope, id);

    public async Task ToUsersAsync(IEnumerable<Guid> userIds, string scope, Guid? id = null)
    {
        foreach (var userId in userIds.Distinct())
            await SendAsync(RealtimeGroups.User(userId), scope, id);
    }

    public Task ToParticipantsAsync(Guid first, Guid second, string scope, Guid? id = null) =>
        ToUsersAsync([first, second], scope, id);

    public Task ToStaffAsync(string scope, Guid? id = null) =>
        SendAsync(RealtimeGroups.Staff, scope, id);

    public Task ToAllAsync(string scope, Guid? id = null) =>
        SendAsync(RealtimeGroups.Users, scope, id);

    private async Task SendAsync(string group, string scope, Guid? id)
    {
        try
        {
            await hub.Clients.Group(group).SendAsync("Changed", new RealtimeChange(scope, id, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime publish failed for {Scope} to {Group}.", scope, group);
        }
    }
}
