using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// In-app notifications. Event-driven notices are written inline by the booking
/// and request endpoints; time-based notices are materialized when an account
/// polls so a delayed poll still surfaces the latest lesson reminder.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this WebApplication app)
    {
        var api = app.MapGroup("/api/notifications").RequireAuthorization();

        api.MapGet("", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var userId = ApiEndpoints.UserId(p);
            var role = RoleOf(p);
            if (role is "teacher" or "student")
                await SyncLessonNoticesAsync(db, userId, role, DateTimeOffset.UtcNow);

            var items = await db.Notifications.AsNoTracking()
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAt)
                .Take(50)
                .Select(x => new NotificationDto(x.Id, x.Title, x.Body, x.Kind, x.Link, x.CreatedAt, x.ReadAt))
                .ToListAsync();
            var unread = await db.Notifications.CountAsync(x => x.UserId == userId && x.ReadAt == null);
            return Results.Ok(new NotificationsDto(items, unread));
        });

        api.MapPost("/{id:guid}/read", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var userId = ApiEndpoints.UserId(p);
            var item = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
            if (item is null) return Results.NotFound();
            if (item.ReadAt is null)
            {
                item.ReadAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        api.MapPost("/read-all", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var userId = ApiEndpoints.UserId(p);
            var now = DateTimeOffset.UtcNow;
            await db.Notifications.Where(x => x.UserId == userId && x.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now));
            return Results.NoContent();
        });
    }

    private static string RoleOf(ClaimsPrincipal p)
    {
        if (p.IsInRole("teacher")) return "teacher";
        if (p.IsInRole("student")) return "student";
        if (p.IsInRole("support")) return "support";
        return "admin";
    }

    private static async Task SyncLessonNoticesAsync(
        AcademyDbContext db,
        Guid userId,
        string role,
        DateTimeOffset now)
    {
        var isTeacher = role == "teacher";
        // The account's poll also materializes reminders, using the same dedupe
        // keys as the background monitor so the two never duplicate each other.
        var leadHorizon = now.AddHours(25);
        var lessons = await db.Lessons.AsNoTracking()
            .Include(x => x.Teacher)
            .Include(x => x.Student)
            .Include(x => x.Report)
            .Where(x =>
                x.Status == "scheduled" &&
                x.EndsAt > now &&
                x.StartsAt <= leadHorizon &&
                (isTeacher ? x.TeacherId == userId : x.StudentId == userId))
            .ToListAsync();

        var pending = new List<AppNotification>();
        foreach (var lesson in lessons)
        {
            var started = lesson.StartsAt <= now;
            var withinHour = lesson.StartsAt - now <= TimeSpan.FromHours(1);
            var otherName = isTeacher
                ? lesson.Student?.FullName ?? "your student"
                : lesson.Teacher?.FullName ?? "your teacher";
            if (started)
            {
                pending.Add(new AppNotification
                {
                    UserId = userId,
                    Title = "Lesson starting now",
                    Body = $"Your lesson with {otherName} is starting now. Tap to open the room.",
                    Kind = "lesson",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"lesson-start:{lesson.Id}",
                });
                continue;
            }

            pending.Add(withinHour
                ? new AppNotification
                {
                    UserId = userId,
                    Title = "Lesson in 1 hour",
                    Body = $"Your lesson with {otherName} starts in 1 hour. Tap to review the details.",
                    Kind = "lesson",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"lesson-reminder-1h:{lesson.Id}",
                }
                : new AppNotification
                {
                    UserId = userId,
                    Title = "Lesson tomorrow",
                    Body = $"Your lesson with {otherName} starts in 24 hours. Tap to review the details.",
                    Kind = "lesson",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"lesson-reminder-24h:{lesson.Id}",
                });
        }

        if (isTeacher)
        {
            var finished = await db.Lessons.AsNoTracking()
                .Include(x => x.Student)
                .Include(x => x.Report)
                .Where(x =>
                    x.TeacherId == userId &&
                    x.Status != "completed" &&
                    x.Status != "cancelled" &&
                    x.EndsAt <= now &&
                    x.EndsAt > now.AddDays(-3) &&
                    x.Report == null)
                .ToListAsync();
            foreach (var lesson in finished)
                pending.Add(new AppNotification
                {
                    UserId = userId,
                    Title = "Lesson report due",
                    Body = $"Your lesson with {lesson.Student?.FullName ?? "your student"} has ended. Submit the report to complete it.",
                    Kind = "report",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"lesson-report:{lesson.Id}",
                });
        }

        if (pending.Count == 0) return;

        var keys = pending.Select(x => x.DedupeKey!).ToList();
        var existing = await db.Notifications.AsNoTracking()
            .Where(x => x.UserId == userId && x.DedupeKey != null && keys.Contains(x.DedupeKey))
            .Select(x => x.DedupeKey!)
            .ToListAsync();
        var seen = existing.ToHashSet(StringComparer.Ordinal);
        var fresh = pending.Where(x => seen.Add(x.DedupeKey!)).ToList();
        if (fresh.Count == 0) return;

        db.Notifications.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another poll inserted the same notices first; the next read sees them.
            db.ChangeTracker.Clear();
        }
    }
}
