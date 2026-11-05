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
    LessonPenBoard penBoard) : Hub
{
    private const string PenDrawerItem = "pen-drawer:";
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Realtime connection {ConnectionId} closed.", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public string Ping() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// Joins the ink group for one lesson and returns what is already on the
    /// board, so late joiners and reconnects see the same marks.
    /// </summary>
    public async Task<PenSnapshot> JoinLessonPen(Guid lessonId)
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

        // Support staff may watch the marks; only the lesson's teacher (or an
        // admin standing in) may draw.
        Context.Items[PenDrawerItem + lessonId] = isAdmin || lesson.TeacherId == userId;
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.LessonPen(lessonId));
        return new PenSnapshot(penBoard.Snapshot(lessonId));
    }

    public Task LeaveLessonPen(Guid lessonId)
    {
        Context.Items.Remove(PenDrawerItem + lessonId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.LessonPen(lessonId));
    }

    public async Task StartPenStroke(
        Guid lessonId, Guid strokeId, string color, double width, double x, double y)
    {
        RequirePenDrawer(lessonId);
        if (!LessonPenBoard.IsAllowedColor(color))
            throw new HubException("Unsupported ink color.");

        var safeWidth = double.IsFinite(width) ? Math.Clamp(width, 0.001, 0.05) : 0.004;
        var safeX = ClampUnit(x);
        var safeY = ClampUnit(y);
        if (!penBoard.Start(lessonId, strokeId, color, safeWidth, safeX, safeY)) return;

        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeStarted", new PenStrokeStart(strokeId, color, safeWidth, safeX, safeY));
    }

    public async Task AppendPenStroke(Guid lessonId, Guid strokeId, double[] points)
    {
        RequirePenDrawer(lessonId);
        if (points is null || points.Length < 2 || points.Length > MaxAppendPoints || points.Length % 2 != 0)
            throw new HubException("Invalid ink points.");

        for (var i = 0; i < points.Length; i++) points[i] = ClampUnit(points[i]);
        if (!penBoard.Append(lessonId, strokeId, points)) return;

        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeAppended", new PenStrokeAppend(strokeId, points));
    }

    public Task EndPenStroke(Guid lessonId, Guid strokeId)
    {
        RequirePenDrawer(lessonId);
        return Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenStrokeEnded", new PenStrokeEnd(strokeId));
    }

    public async Task ClearLessonPen(Guid lessonId)
    {
        RequirePenDrawer(lessonId);
        penBoard.Clear(lessonId);
        await Clients.OthersInGroup(RealtimeGroups.LessonPen(lessonId))
            .SendAsync("PenCleared");
    }

    private void RequirePenDrawer(Guid lessonId)
    {
        if (!Context.Items.TryGetValue(PenDrawerItem + lessonId, out var value) || value is not true)
            throw new HubException("Only the teacher can mark this lesson.");
    }

    private static double ClampUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
