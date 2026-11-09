using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// Clients never choose their groups. Membership is derived from the ticket's
/// authenticated claims, so a student cannot subscribe to another lesson.
/// </summary>
[Authorize(AuthenticationSchemes = RealtimeTicketDefaults.Scheme)]
public sealed class RealtimeHub(
    ILogger<RealtimeHub> logger,
    AcademyDbContext db,
    LessonPenBoard penBoard,
    LessonShareBoard shareBoard) : Hub
{
    private const string PenDrawerItem = "pen-drawer:";
    private const string ShareMemberItem = "lesson-share-member:";
    private const string ShareNameItem = "lesson-share-name:";
    private const int MaxAppendPoints = 256;

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

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A share held by a connection that vanished is not held by anyone.
        foreach (var lessonId in shareBoard.ReleaseConnection(Context.ConnectionId))
            await Clients.OthersInGroup(RealtimeGroups.LessonShare(lessonId))
                .SendAsync("LessonShareChanged", new LessonShareChanged(false, null));

        logger.LogDebug("Realtime connection {ConnectionId} closed.", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public string Ping() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// Joins the ink group for one lesson and returns what is already on the
    /// board, so late joiners and reconnects see the same marks.
    /// </summary>
    public async Task<PenSnapshot> JoinLessonPen(Guid lessonId)
    {
        var access = await RequireLessonAccessAsync(lessonId);

        // Support staff may watch the marks; only the lesson's teacher (or an
        // admin standing in) may draw.
        Context.Items[PenDrawerItem + lessonId] = access.IsAdmin || access.IsTeacher;
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.LessonPen(lessonId));
        return penBoard.Snapshot(lessonId);
    }

    public Task LeaveLessonPen(Guid lessonId)
    {
        Context.Items.Remove(PenDrawerItem + lessonId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.LessonPen(lessonId));
    }

    public async Task StartPenStroke(
        Guid lessonId, string? strokeId, string color, double width, double x, double y)
    {
        RequirePenDrawer(lessonId);
        if (!LessonPenBoard.IsAllowedStrokeId(strokeId))
            throw new HubException("Invalid ink stroke id.");
        if (!LessonPenBoard.IsAllowedColor(color))
            throw new HubException("Unsupported ink color.");

        var safeWidth = double.IsFinite(width) ? Math.Clamp(width, 0.001, 0.05) : 0.004;
        var safeX = ClampUnit(x);
        var safeY = ClampUnit(y);
        if (!penBoard.Start(lessonId, strokeId!, color, safeWidth, safeX, safeY)) return;

        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeStarted", new PenStrokeStart(strokeId!, color, safeWidth, safeX, safeY));
    }

    public async Task AppendPenStroke(Guid lessonId, string? strokeId, double[] points)
    {
        RequirePenDrawer(lessonId);
        if (!LessonPenBoard.IsAllowedStrokeId(strokeId))
            throw new HubException("Invalid ink stroke id.");
        if (points is null || points.Length < 2 || points.Length > MaxAppendPoints || points.Length % 2 != 0)
            throw new HubException("Invalid ink points.");

        for (var i = 0; i < points.Length; i++) points[i] = ClampUnit(points[i]);
        if (!penBoard.Append(lessonId, strokeId!, points)) return;

        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeAppended", new PenStrokeAppend(strokeId!, points));
    }

    public Task EndPenStroke(Guid lessonId, string? strokeId)
    {
        RequirePenDrawer(lessonId);
        if (!LessonPenBoard.IsAllowedStrokeId(strokeId))
            throw new HubException("Invalid ink stroke id.");
        return Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeEnded", new PenStrokeEnd(strokeId!));
    }

    public async Task ClearLessonPen(Guid lessonId)
    {
        RequirePenDrawer(lessonId);
        penBoard.Clear(lessonId);
        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenCleared");
    }

    /// <summary>
    /// Fits the lesson's annotation frame to the shared picture. Only the width
    /// to height ratio travels: each screen re-centres it in its own call view,
    /// so marks stay on the page across a desktop and a portrait phone.
    /// </summary>
    public async Task<double> SetPenFrame(Guid lessonId, double aspect)
    {
        RequirePenDrawer(lessonId);
        var safe = penBoard.SetFrame(lessonId, aspect);
        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenFrameChanged", new PenFrameChanged(safe));
        return safe;
    }

    /// <summary>
    /// Subscribes to the lesson's screen-share lock and reports who holds it
    /// right now, so someone joining mid-lesson learns the share is taken.
    /// </summary>
    public async Task<LessonShareChanged> JoinLessonShare(Guid lessonId)
    {
        var access = await RequireLessonAccessAsync(lessonId);

        // Labelled from the other participant's point of view, because that is
        // the only audience for it. Scrubbed names never reach the room.
        var fullName = await db.Users.AsNoTracking()
            .Where(x => x.Id == access.UserId)
            .Select(x => x.FullName)
            .FirstOrDefaultAsync();
        var label = ApiEndpoints.DisplayName(fullName)
            ?? (access.IsAdmin ? "An admin" : access.IsTeacher ? "Your teacher" : "Your student");

        Context.Items[ShareMemberItem + lessonId] = true;
        Context.Items[ShareNameItem + lessonId] = label;
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.LessonShare(lessonId));

        var current = shareBoard.Current(lessonId);
        return new LessonShareChanged(current is not null, current?.DisplayName);
    }

    public Task LeaveLessonShare(Guid lessonId)
    {
        Context.Items.Remove(ShareMemberItem + lessonId);
        Context.Items.Remove(ShareNameItem + lessonId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.LessonShare(lessonId));
    }

    /// <summary>
    /// Takes the share for this lesson, or reports who already holds it. The
    /// holder may re-claim; a second participant cannot displace them.
    /// </summary>
    public async Task<LessonShareClaim> ClaimLessonShare(Guid lessonId)
    {
        if (!Context.Items.TryGetValue(ShareMemberItem + lessonId, out var member) || member is not true)
            throw new HubException("You are not part of this lesson.");
        var label = Context.Items[ShareNameItem + lessonId] as string
            ?? throw new HubException("Join the lesson screen share first.");

        var claim = shareBoard.Claim(lessonId, new LessonShareHolder(Context.ConnectionId, label));
        if (claim.Accepted)
            await Clients.OthersInGroup(RealtimeGroups.LessonShare(lessonId))
                .SendAsync("LessonShareChanged", new LessonShareChanged(true, claim.Holder));

        return claim;
    }

    public async Task ReleaseLessonShare(Guid lessonId)
    {
        if (!shareBoard.Release(lessonId, Context.ConnectionId)) return;
        await Clients.OthersInGroup(RealtimeGroups.LessonShare(lessonId))
            .SendAsync("LessonShareChanged", new LessonShareChanged(false, null));
    }

    private sealed record LessonAccess(Guid UserId, bool IsAdmin, bool IsTeacher);

    private async Task<LessonAccess> RequireLessonAccessAsync(Guid lessonId)
    {
        var raw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var userId))
            throw new HubException("You are not signed in.");

        var lesson = await db.Lessons.AsNoTracking()
            .Where(x => x.Id == lessonId)
            .Select(x => new { x.TeacherId, x.StudentId })
            .FirstOrDefaultAsync();
        if (lesson is null) throw new HubException("Lesson not found.");

        var isAdmin = Context.User!.IsInRole("admin") || Context.User.IsInRole("super_admin");
        var isSupport = Context.User.IsInRole("support");
        if (!isAdmin && !isSupport && lesson.TeacherId != userId && lesson.StudentId != userId)
            throw new HubException("You are not part of this lesson.");

        return new LessonAccess(userId, isAdmin, lesson.TeacherId == userId);
    }

    private void RequirePenDrawer(Guid lessonId)
    {
        if (!Context.Items.TryGetValue(PenDrawerItem + lessonId, out var value) || value is not true)
            throw new HubException("Only the teacher can mark this lesson.");
    }

    private static double ClampUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
