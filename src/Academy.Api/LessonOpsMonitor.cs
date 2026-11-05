using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// Periodically closes attendance windows, raises potential no-show incidents,
/// finalizes teacher hours, and sends server-side lesson reminders so push
/// delivery does not depend on a client polling.
/// </summary>
public sealed class LessonOpsMonitor(IServiceScopeFactory scopes, ILogger<LessonOpsMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lesson operations monitor tick failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AcademyDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<AcademyPushSender>();
        var now = DateTimeOffset.UtcNow;
        var policy = await LessonPolicyService.GetPolicyAsync(db, ct);

        // Requests that ran out of time without a reply.
        await db.LessonRescheduleRequests
            .Where(x => x.Status == "pending" && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "expired"), ct);

        var pushJobs = new List<(Guid UserId, string Title, string Body, string Link)>();
        var adminIds = new List<Guid>();
        var newIncidents = new List<(Lesson Lesson, LessonIncident Incident)>();

        // Lessons that ended recently still need their attendance closed and hours
        // finalized. Two days is enough to catch a device that never sent "leave".
        var ended = await db.Lessons
            .Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report)
            .Where(x => x.Status != "cancelled" && x.EndsAt <= now && x.EndsAt > now.AddDays(-2))
            .OrderByDescending(x => x.EndsAt).Take(300).ToListAsync(ct);

        foreach (var lesson in ended)
        {
            await LessonPolicyService.CloseAttendanceAsync(db, lesson, now, policy, ct);
            var attendances = await db.LessonAttendances
                .Where(x => x.LessonId == lesson.Id).ToListAsync(ct);
            var teacherPresent = attendances.Any(x => x.UserId == lesson.TeacherId && x.TotalSeconds > 0);
            var studentPresent = attendances.Any(x => x.UserId == lesson.StudentId && x.TotalSeconds > 0);

            if (teacherPresent && studentPresent)
            {
                var open = await db.LessonIncidents
                    .Where(x => x.LessonId == lesson.Id && x.Status == "open"
                        && (x.Type == "potential_teacher_no_show" || x.Type == "potential_student_no_show" || x.Type == "both_absent"))
                    .ToListAsync(ct);
                foreach (var incident in open)
                {
                    incident.Status = "dismissed";
                    incident.ResolvedAt = now;
                    incident.ResolutionNote = "Both participants joined before the lesson ended; resolved automatically.";
                }
            }
            else
            {
                var type = !teacherPresent && !studentPresent ? "both_absent"
                    : !teacherPresent ? "potential_teacher_no_show"
                    : "potential_student_no_show";
                var existing = await db.LessonIncidents
                    .AnyAsync(x => x.LessonId == lesson.Id && x.Type == type, ct);
                if (!existing)
                {
                    var incident = new LessonIncident
                    {
                        LessonId = lesson.Id,
                        Type = type,
                        Detail = type switch
                        {
                            "both_absent" => "Neither the teacher nor the student joined the lesson room.",
                            "potential_teacher_no_show" => "The student joined but the teacher never joined the lesson room.",
                            _ => "The teacher joined but the student never joined the lesson room.",
                        },
                    };
                    db.LessonIncidents.Add(incident);
                    newIncidents.Add((lesson, incident));
                }
            }

            if (lesson.Report is null && lesson.Status != "completed")
                await LessonPolicyService.EnsureTeacherHoursAsync(db, lesson, policy, now, ct: ct);
        }

        // Live lessons: flag a missing participant once the grace period passes.
        var graceCutoff = now.AddMinutes(-policy.NoShowGraceMinutes);
        var live = await db.Lessons
            .Include(x => x.Teacher).Include(x => x.Student)
            .Where(x => x.Status == "scheduled"
                && x.StartsAt <= graceCutoff
                && x.EndsAt > now)
            .Take(200).ToListAsync(ct);
        foreach (var lesson in live)
        {
            var attendances = await db.LessonAttendances.AsNoTracking()
                .Where(x => x.LessonId == lesson.Id).ToListAsync(ct);
            var teacherPresent = attendances.Any(x => x.UserId == lesson.TeacherId && x.TotalSeconds > 0);
            var studentPresent = attendances.Any(x => x.UserId == lesson.StudentId && x.TotalSeconds > 0);
            if (teacherPresent == studentPresent) continue;

            var type = teacherPresent ? "potential_student_no_show" : "potential_teacher_no_show";
            if (await db.LessonIncidents.AnyAsync(x => x.LessonId == lesson.Id && x.Type == type, ct)) continue;
            var incident = new LessonIncident
            {
                LessonId = lesson.Id,
                Type = type,
                Detail = teacherPresent
                    ? "The teacher joined the lesson but the student has not joined yet."
                    : "The student joined the lesson but the teacher has not joined yet.",
            };
            db.LessonIncidents.Add(incident);
            newIncidents.Add((lesson, incident));
        }

        // Upcoming reminders, pushed server-side even when the app is closed.
        var upcoming = await db.Lessons
            .Include(x => x.Teacher).Include(x => x.Student)
            .Where(x => x.Status == "scheduled" && x.StartsAt <= now.AddHours(25) && x.EndsAt > now)
            .OrderBy(x => x.StartsAt).Take(300).ToListAsync(ct);
        foreach (var lesson in upcoming)
        {
            var started = lesson.StartsAt <= now;
            var withinHour = lesson.StartsAt - now <= TimeSpan.FromHours(1);
            var (key, title, body) = started
                ? ($"lesson-start:{lesson.Id}", "Lesson starting now",
                    $"Your lesson with {{0}} is starting now. Tap to open the room.")
                : withinHour
                    ? ($"lesson-reminder-1h:{lesson.Id}", "Lesson in 1 hour",
                        $"Your lesson with {{0}} starts in 1 hour. Tap to review the details.")
                    : ($"lesson-reminder-24h:{lesson.Id}", "Lesson tomorrow",
                        $"Your lesson with {{0}} starts in 24 hours. Tap to review the details.");

            foreach (var (userId, otherName) in new[]
            {
                (lesson.TeacherId, lesson.Student?.FullName ?? "your student"),
                (lesson.StudentId, lesson.Teacher?.FullName ?? "your teacher"),
            })
            {
                if (await db.Notifications.AnyAsync(x => x.UserId == userId && x.DedupeKey == key, ct)) continue;
                var message = body.Replace("{0}", otherName);
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    userId, title, message, "lesson", $"/lesson/{lesson.Id}", key));
                pushJobs.Add((userId, title, message, $"/lesson/{lesson.Id}"));
            }
        }

        // Report-due push for teachers, once per lesson.
        var pendingReports = await db.Lessons
            .Include(x => x.Student).Include(x => x.Report)
            .Where(x => x.Status != "cancelled" && x.Status != "completed"
                && x.EndsAt <= now && x.EndsAt > now.AddDays(-3) && x.Report == null)
            .Take(200).ToListAsync(ct);
        foreach (var lesson in pendingReports)
        {
            var key = $"lesson-report:{lesson.Id}";
            if (await db.Notifications.AnyAsync(x => x.UserId == lesson.TeacherId && x.DedupeKey == key, ct)) continue;
            var body = $"Your lesson with {lesson.Student?.FullName ?? "your student"} has ended. Submit the report to complete it.";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                lesson.TeacherId, "Lesson report due", body, "report", $"/lesson/{lesson.Id}", key));
            pushJobs.Add((lesson.TeacherId, "Lesson report due", body, $"/lesson/{lesson.Id}"));
        }

        if (newIncidents.Count > 0)
        {
            adminIds = await LessonOpsMessaging.AdminUserIdsAsync(db, ct);
            foreach (var (lesson, incident) in newIncidents)
            {
                var title = incident.Type switch
                {
                    "both_absent" => "Both participants missed a lesson",
                    "potential_teacher_no_show" => "Possible teacher no-show",
                    _ => "Possible student no-show",
                };
                foreach (var adminId in adminIds)
                    db.Notifications.Add(LessonOpsMessaging.Notice(
                        adminId, title,
                        $"{lesson.Teacher?.FullName ?? "Teacher"} / {lesson.Student?.FullName ?? "Student"} on {lesson.StartsAt:ddd, MMM d 'at' h:mm tt}: {incident.Detail}",
                        "system", "/admin/lesson-ops", $"incident-admin:{incident.Id}:{adminId}"));
                pushJobs.AddRange(adminIds.Select(adminId => (adminId, title, incident.Detail, "/admin/lesson-ops")));
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent poll may have inserted the same reminder first.
                db.ChangeTracker.Clear();
                logger.LogDebug(ex, "Lesson operations monitor skipped a duplicate notification.");
            }
        }

        foreach (var job in pushJobs)
            await LessonOpsMessaging.PushAsync(db, push, [job.UserId], job.Title, job.Body, job.Link, ct);
    }
}
