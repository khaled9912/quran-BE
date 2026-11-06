using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// Backend-authoritative lesson operations. Clients may preview a cancellation,
/// but every state change is re-evaluated here against the current policy.
/// </summary>
public static class LessonOperationsEndpoints
{
    private static Guid UserId(ClaimsPrincipal p) => ApiEndpoints.UserId(p);
    private static bool IsAdmin(ClaimsPrincipal p) => p.IsInRole("admin") || p.IsInRole("super_admin");
    private static string ActorRole(ClaimsPrincipal p)
    {
        if (p.IsInRole("student")) return "student";
        if (p.IsInRole("teacher")) return "teacher";
        if (p.IsInRole("support")) return "support";
        return "admin";
    }

    private static Task<Lesson?> LoadLessonAsync(AcademyDbContext db, Guid id, CancellationToken ct) =>
        db.Lessons
            .Include(x => x.Teacher)
            .Include(x => x.Student)
            .Include(x => x.Series)
            .Include(x => x.Report)
            .Include(x => x.Review)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

    private static bool CanView(Lesson lesson, ClaimsPrincipal p) =>
        IsAdmin(p) || p.IsInRole("support")
        || lesson.TeacherId == UserId(p) || lesson.StudentId == UserId(p);

    private static Task<bool> HasConflictAsync(
        AcademyDbContext db,
        Guid lessonId,
        Guid teacherId,
        Guid studentId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        CancellationToken ct) =>
        db.Lessons.AnyAsync(x =>
            x.Id != lessonId
            && x.Status != "cancelled"
            && x.StartsAt < endsAt
            && x.EndsAt > startsAt
            && (x.TeacherId == teacherId || x.StudentId == studentId), ct);

    private static void Audit(AcademyDbContext db, Guid? actorId, string action, string entityType, Guid? entityId, string detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail.Length > 2000 ? detail[..2000] : detail,
        });

    private static async Task<Dictionary<Guid, string>> NamesAsync(
        AcademyDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Users.AsNoTracking()
            .Where(x => list.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
    }

    private static LessonCancellationDto ToCancellation(LessonCancellation c) => new(
        c.Id, c.LessonId, c.ActorRole, c.Outcome, c.IsLate, c.UsedGrace,
        c.HoursBefore, c.TeacherPayableMinutes, c.Reason, c.CreatedAt);

    private static RescheduleRequestDto ToReschedule(
        LessonRescheduleRequest r, string requesterName, DateTimeOffset now) => new(
        r.Id, r.LessonId, r.RequestedById, requesterName, r.RequestedByRole,
        r.ProposedStartsAt, r.ProposedEndsAt, r.Note, r.Status, r.ExpiresAt,
        r.RespondedById, r.RespondedAt, r.ResponseNote, r.CreatedAt,
        Math.Max(0, (int)Math.Ceiling((r.ExpiresAt - now).TotalMinutes)));

    private static ExcuseRequestDto ToExcuse(LessonExcuseRequest r) => new(
        r.Id, r.LessonId, r.StudentId, r.Student?.FullName ?? "Student",
        r.Lesson?.StartsAt ?? default, r.Category, r.Explanation, r.Status,
        r.AdminNote, r.CreatedAt, r.ReviewedAt);

    private static TeacherHoursDto ToHours(TeacherHours h, Lesson l) => new(
        h.Id, h.LessonId, h.TeacherId, l.Teacher?.FullName ?? "Teacher",
        h.Lesson?.StudentId ?? l.StudentId, l.Student?.FullName ?? "Student",
        l.StartsAt, l.EndsAt, l.Status, h.ScheduledMinutes, h.ActualAttendanceMinutes,
        h.ProposedPayableMinutes, h.ApprovedPayableMinutes, h.ApprovalStatus,
        h.AdjustmentReason, h.ApprovedAt);

    private static LessonIncidentDto ToIncident(LessonIncident i, Lesson l) => new(
        i.Id, i.LessonId, l.StartsAt, l.TeacherId, l.Teacher?.FullName ?? "Teacher",
        l.StudentId, l.Student?.FullName ?? "Student", i.Type, i.Status, i.Detail,
        i.CreatedAt, i.ResolvedAt, i.ResolutionNote);

    private static TeacherPenaltyDto ToPenalty(TeacherPenalty x) => new(
        x.Id, x.TeacherId, x.Teacher?.FullName ?? "Teacher", x.LessonId, x.EventType,
        x.AmountEgp, x.Reason, x.Status, x.AdminNote, x.CreatedAt, x.ReviewedAt);

    private static ReliabilityEventDto ToReliability(ReliabilityEvent x) => new(
        x.Id, x.UserId, x.User?.FullName ?? "User", x.Role, x.LessonId, x.Type, x.Notes,
        x.CreatedAt, x.ResolvedAt, x.ResolutionNote, x.ResolutionAction);

    private static LessonCreditTransactionDto ToCredit(LessonCreditTransaction x) => new(
        x.Id, x.StudentId, x.LessonId, x.Type, x.Amount, x.Reason, x.CreatedByRole, x.CreatedAt);

    private static SalaryPeriodDto ToSalary(SalaryPeriod x) => new(
        x.Id, x.TeacherId, x.Teacher?.FullName ?? "Teacher", x.Month, x.Status,
        x.TotalScheduledMinutes, x.TotalActualMinutes, x.TotalProposedMinutes,
        x.TotalApprovedMinutes, x.ApprovedPenaltyEgp, x.PendingPenaltyEgp,
        x.CompletedLessons, x.StudentLateCancellations, x.TeacherCancellations,
        x.TeacherNoShows, x.StudentNoShows, x.ManualAdjustmentEgp, x.FinalAmountEgp,
        x.Notes, x.ReviewedAt, x.PaidAt);

    private static LessonScheduleChangeDto ToScheduleChange(LessonScheduleChange x) => new(
        x.Id, x.FromStartsAt, x.FromEndsAt, x.ToStartsAt, x.ToEndsAt,
        x.ChangedById, x.Reason, x.CreatedAt);

    private static LessonCallDto ToCall(LessonCall x) => new(
        x.Id, x.LessonId, x.CallerId, x.Caller?.FullName ?? "User", x.CallerRole,
        x.CalleeId, x.Callee?.FullName ?? "User", x.CalleeRole, x.Origin,
        x.CalleeStatus, x.CreatedAt);

    /// <summary>A ring is a interruption, not a chat: it can be sent at most
    /// this often per lesson, and never twice in a row within the cooldown.</summary>
    private static readonly TimeSpan CallCooldown = TimeSpan.FromSeconds(30);
    private const int CallsPerLesson = 3;
    private static readonly TimeSpan CallLead = TimeSpan.FromMinutes(15);

    public static void MapLessonOperations(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // ---------------------------------------------------------------- policy
        api.MapGet("/lesson-policy", async (AcademyDbContext db, CancellationToken ct) =>
            Results.Ok(LessonPolicyService.ToDto(await LessonPolicyService.GetPolicyAsync(db, ct))));

        api.MapPut("/lesson-policy", async (UpdateAcademyPolicyRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var errors = new Dictionary<string, string[]>();
            void Check(string name, int? value, int min, int max)
            {
                if (value is not null && (value < min || value > max))
                    errors[name] = [$"Enter a value from {min} to {max}."];
            }
            Check("studentFreeCancelHours", input.StudentFreeCancelHours, 0, 336);
            Check("teacherFreeCancelHours", input.TeacherFreeCancelHours, 0, 336);
            Check("noShowGraceMinutes", input.NoShowGraceMinutes, 0, 120);
            Check("rescheduleResponseHours", input.RescheduleResponseHours, 1, 168);
            Check("studentGraceCancellationsPerMonth", input.StudentGraceCancellationsPerMonth, 0, 10);
            Check("studentWarningStrikeThreshold", input.StudentWarningStrikeThreshold, 1, 20);
            Check("studentAdminReviewStrikeThreshold", input.StudentAdminReviewStrikeThreshold, 1, 20);
            Check("teacherAdminReviewStrikeThreshold", input.TeacherAdminReviewStrikeThreshold, 1, 20);
            Check("trialNoShowThreshold", input.TrialNoShowThreshold, 1, 10);
            var warning = input.StudentWarningStrikeThreshold ?? policy.StudentWarningStrikeThreshold;
            var review = input.StudentAdminReviewStrikeThreshold ?? policy.StudentAdminReviewStrikeThreshold;
            if (review < warning)
                errors["studentAdminReviewStrikeThreshold"] = ["Admin review must happen at or after the warning threshold."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (input.StudentFreeCancelHours is not null) policy.StudentFreeCancelHours = input.StudentFreeCancelHours.Value;
            if (input.TeacherFreeCancelHours is not null) policy.TeacherFreeCancelHours = input.TeacherFreeCancelHours.Value;
            if (input.NoShowGraceMinutes is not null) policy.NoShowGraceMinutes = input.NoShowGraceMinutes.Value;
            if (input.RescheduleResponseHours is not null) policy.RescheduleResponseHours = input.RescheduleResponseHours.Value;
            if (input.StudentGraceCancellationsPerMonth is not null) policy.StudentGraceCancellationsPerMonth = input.StudentGraceCancellationsPerMonth.Value;
            if (input.StudentWarningStrikeThreshold is not null) policy.StudentWarningStrikeThreshold = input.StudentWarningStrikeThreshold.Value;
            if (input.StudentAdminReviewStrikeThreshold is not null) policy.StudentAdminReviewStrikeThreshold = input.StudentAdminReviewStrikeThreshold.Value;
            if (input.TeacherAdminReviewStrikeThreshold is not null) policy.TeacherAdminReviewStrikeThreshold = input.TeacherAdminReviewStrikeThreshold.Value;
            if (input.TrialNoShowThreshold is not null) policy.TrialNoShowThreshold = input.TrialNoShowThreshold.Value;
            if (input.PayScheduledDurationWhenAttended is not null) policy.PayScheduledDurationWhenAttended = input.PayScheduledDurationWhenAttended.Value;
            policy.UpdatedAt = DateTimeOffset.UtcNow;
            policy.UpdatedBy = UserId(p);
            Audit(db, UserId(p), "policy_update", "AcademyPolicy", null, "Lesson cancellation and compensation policy updated.");
            await db.SaveChangesAsync(ct);
            await realtime.ToAllAsync(RealtimeScopes.Policy);
            return Results.Ok(LessonPolicyService.ToDto(policy));
        }).RequireAuthorization("AdminOnly");

        // ------------------------------------------------------------ operations
        api.MapGet("/lessons/{id:guid}/operations", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var decision = await LessonPolicyService.EvaluateCancellationAsync(db, lesson, ActorRole(p), now, policy, ct);
            var cancellation = await db.LessonCancellations.AsNoTracking().FirstOrDefaultAsync(x => x.LessonId == id, ct);
            var reschedules = await db.LessonRescheduleRequests.AsNoTracking()
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var names = await NamesAsync(db, reschedules.Select(x => x.RequestedById), ct);
            var mapped = reschedules.Select(x => ToReschedule(x, names.GetValueOrDefault(x.RequestedById, "User"), now)).ToList();
            var attendance = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
            var hours = await db.TeacherHours.AsNoTracking().FirstOrDefaultAsync(x => x.LessonId == id, ct);
            var excuse = await db.LessonExcuseRequests.AsNoTracking().Include(x => x.Student).Include(x => x.Lesson)
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var incident = await db.LessonIncidents.AsNoTracking()
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var calls = await db.LessonCalls.AsNoTracking()
                .Include(x => x.Caller).Include(x => x.Callee)
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct);
            var replacement = lesson.IsMakeup ? null : await db.Lessons.AsNoTracking()
                .Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review)
                .FirstOrDefaultAsync(x => x.OriginalLessonId == id && x.Status != "cancelled", ct);
            var original = lesson.OriginalLessonId is Guid originalId
                ? await db.Lessons.AsNoTracking().Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review)
                    .FirstOrDefaultAsync(x => x.Id == originalId, ct)
                : null;

            return Results.Ok(new LessonOperationsDto(
                id, decision, cancellation is null ? null : ToCancellation(cancellation),
                mapped.FirstOrDefault(x => x.Status == "pending"), mapped, attendance,
                hours is null ? null : ToHours(hours, lesson),
                excuse is null ? null : ToExcuse(excuse),
                incident is null ? null : ToIncident(incident, lesson),
                replacement is null ? null : ApiEndpoints.ToLesson(replacement),
                original is null ? null : ApiEndpoints.ToLesson(original),
                calls.Select(ToCall).ToList()));
        });

        api.MapGet("/lessons/{id:guid}/cancellation-preview", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var decision = await LessonPolicyService.EvaluateCancellationAsync(
                db, lesson, ActorRole(p), DateTimeOffset.UtcNow, policy, ct);
            return Results.Ok(decision);
        });

        api.MapPost("/lessons/{id:guid}/cancel", (Guid id, CancelLessonRequest? input, ClaimsPrincipal p,
            AcademyDbContext db, AcademyEmailSender email, AcademyPushSender push, RealtimeNotifier realtime,
            CancellationToken ct) =>
            CancelAsync(id, input?.Reason, p, db, email, push, realtime, ct));

        // ------------------------------------------------------------ reschedule
        api.MapGet("/lessons/{id:guid}/reschedule-requests", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var requests = await db.LessonRescheduleRequests.AsNoTracking()
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var names = await NamesAsync(db, requests.Select(x => x.RequestedById), ct);
            return Results.Ok(requests.Select(x => ToReschedule(x, names.GetValueOrDefault(x.RequestedById, "User"), now)));
        });

        api.MapPost("/lessons/{id:guid}/reschedule-requests", async (
            Guid id, CreateRescheduleRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyEmailSender email, AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            var role = ActorRole(p);
            var participant = lesson.TeacherId == UserId(p) || lesson.StudentId == UserId(p);
            if (!participant && !IsAdmin(p)) return Results.Forbid();
            if (lesson.Status != "scheduled" || lesson.EndsAt <= DateTimeOffset.UtcNow)
                return Results.Conflict(new { message = "Only a scheduled, upcoming lesson can be rescheduled." });

            var now = DateTimeOffset.UtcNow;
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var proposedStarts = input.StartsAt.ToUniversalTime();
            var proposedEnds = input.EndsAt.ToUniversalTime();
            if (proposedStarts <= now)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startsAt"] = ["Choose a future time."] });
            if (proposedEnds <= proposedStarts)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = ["The lesson must end after it starts."] });

            var originalMinutes = LessonPolicyService.ScheduledMinutes(lesson);
            var proposedMinutes = (int)Math.Round((proposedEnds - proposedStarts).TotalMinutes);
            if (Math.Abs(proposedMinutes - originalMinutes) > 1)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = [$"A make-up lesson keeps the same {originalMinutes}-minute length."] });
            if (await HasConflictAsync(db, lesson.Id, lesson.TeacherId, lesson.StudentId, proposedStarts, proposedEnds, ct))
                return Results.Conflict(new { message = "The teacher or student already has a lesson at that time." });

            var pending = await db.LessonRescheduleRequests
                .Where(x => x.LessonId == id && x.Status == "pending").ToListAsync(ct);
            foreach (var stale in pending.Where(x => x.ExpiresAt <= now)) stale.Status = "expired";
            if (pending.Any(x => x.Status == "pending"))
                return Results.Conflict(new { message = "A reschedule request is already waiting for a reply." });

            var note = (input.Note ?? "").Trim();
            if (note.Length > 1000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Keep the note under 1000 characters."] });

            var request = new LessonRescheduleRequest
            {
                LessonId = lesson.Id,
                RequestedById = UserId(p),
                RequestedByRole = participant ? role : "admin",
                ProposedStartsAt = proposedStarts,
                ProposedEndsAt = proposedEnds,
                Note = note.Length == 0 ? null : note,
                ExpiresAt = now.AddHours(policy.RescheduleResponseHours),
            };
            db.LessonRescheduleRequests.Add(request);

            var otherId = role == "teacher" ? lesson.StudentId : lesson.TeacherId;
            var requesterName = role == "teacher"
                ? lesson.Teacher?.FullName ?? "Your teacher"
                : role == "student" ? lesson.Student?.FullName ?? "Your student" : "The academy";
            var body = $"{requesterName} proposed moving the lesson to {LessonOpsMessaging.When(proposedStarts, lesson.Series?.TimeZoneId)}. Reply within {policy.RescheduleResponseHours} hours.";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                otherId, "Reschedule request", body, "lesson", $"/lesson/{lesson.Id}", $"reschedule:{request.Id}"));
            Audit(db, UserId(p), "reschedule_requested", "Lesson", lesson.Id,
                $"Proposed {proposedStarts:u} to {proposedEnds:u}.");
            await db.SaveChangesAsync(ct);

            await LessonOpsMessaging.EmailAsync(email, role == "teacher" ? lesson.Student : lesson.Teacher,
                "Lesson reschedule request", body, ct);
            await LessonOpsMessaging.PushAsync(db, push, [otherId], "Reschedule request", body, $"/lesson/{lesson.Id}", ct);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);

            return Results.Ok(ToReschedule(request, requesterName, now));
        });

        api.MapPost("/reschedule-requests/{requestId:guid}/accept", async (
            Guid requestId, RespondRescheduleRequest? input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyEmailSender email, AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var request = await db.LessonRescheduleRequests
                .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .Include(x => x.Lesson).ThenInclude(x => x!.Series)
                .FirstOrDefaultAsync(x => x.Id == requestId, ct);
            if (request?.Lesson is null) return Results.NotFound();
            var lesson = request.Lesson;
            var now = DateTimeOffset.UtcNow;
            if (request.Status == "pending" && request.ExpiresAt <= now)
            {
                request.Status = "expired";
                await db.SaveChangesAsync(ct);
            }
            if (request.Status != "pending")
                return Results.Conflict(new { message = "This reschedule request is no longer open." });
            if (lesson.Status != "scheduled" || lesson.EndsAt <= now)
            {
                request.Status = "cancelled";
                request.RespondedAt = now;
                await db.SaveChangesAsync(ct);
                return Results.Conflict(new { message = "This lesson is no longer open for rescheduling." });
            }

            var counterpart = request.RequestedById == lesson.TeacherId ? lesson.StudentId
                : request.RequestedById == lesson.StudentId ? lesson.TeacherId
                : Guid.Empty;
            if (UserId(p) != counterpart && !IsAdmin(p)) return Results.Forbid();
            if (UserId(p) == request.RequestedById) return Results.Forbid();
            if (request.ProposedStartsAt <= now)
                return Results.Conflict(new { message = "The proposed time has already passed. Ask for a new time." });
            if (await HasConflictAsync(db, lesson.Id, lesson.TeacherId, lesson.StudentId,
                    request.ProposedStartsAt, request.ProposedEndsAt, ct))
                return Results.Conflict(new { message = "The teacher or student now has another lesson at that time." });

            var previous = (lesson.StartsAt, lesson.EndsAt);
            lesson.StartsAt = request.ProposedStartsAt;
            lesson.EndsAt = request.ProposedEndsAt;
            request.Status = "accepted";
            request.RespondedById = UserId(p);
            request.RespondedAt = now;
            request.ResponseNote = (input?.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            db.LessonScheduleChanges.Add(new LessonScheduleChange
            {
                LessonId = lesson.Id,
                FromStartsAt = previous.StartsAt,
                FromEndsAt = previous.EndsAt,
                ToStartsAt = lesson.StartsAt,
                ToEndsAt = lesson.EndsAt,
                ChangedById = UserId(p),
                Reason = "Reschedule request accepted",
            });
            var movedTo = LessonOpsMessaging.When(lesson.StartsAt, lesson.Series?.TimeZoneId);
            db.Notifications.Add(LessonOpsMessaging.Notice(
                request.RequestedById, "Reschedule accepted",
                $"Your lesson moved to {movedTo}.",
                "lesson", $"/lesson/{lesson.Id}", $"reschedule-accepted:{request.Id}"));
            Audit(db, UserId(p), "reschedule_accepted", "Lesson", lesson.Id,
                $"Moved from {previous.StartsAt:u} to {lesson.StartsAt:u}.");
            await db.SaveChangesAsync(ct);

            await LessonOpsMessaging.PushAsync(db, push, [request.RequestedById], "Reschedule accepted",
                $"Your lesson moved to {movedTo}.", $"/lesson/{lesson.Id}", ct);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            return Results.Ok(ToReschedule(request, "User", now));
        });

        api.MapPost("/reschedule-requests/{requestId:guid}/decline", async (
            Guid requestId, RespondRescheduleRequest? input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var request = await db.LessonRescheduleRequests.Include(x => x.Lesson)
                .FirstOrDefaultAsync(x => x.Id == requestId, ct);
            if (request?.Lesson is null) return Results.NotFound();
            if (request.Status != "pending")
                return Results.Conflict(new { message = "This reschedule request is no longer open." });
            var lesson = request.Lesson;
            var counterpart = request.RequestedById == lesson.TeacherId ? lesson.StudentId
                : request.RequestedById == lesson.StudentId ? lesson.TeacherId
                : Guid.Empty;
            if (UserId(p) != counterpart && !IsAdmin(p)) return Results.Forbid();
            if (UserId(p) == request.RequestedById) return Results.Forbid();

            var note = (input?.Note ?? "").Trim();
            request.Status = "declined";
            request.RespondedById = UserId(p);
            request.RespondedAt = DateTimeOffset.UtcNow;
            request.ResponseNote = note.Length == 0 ? null : note[..Math.Min(note.Length, 500)];
            db.Notifications.Add(LessonOpsMessaging.Notice(
                request.RequestedById, "Reschedule declined",
                note.Length == 0 ? "The proposed new time was declined. Your lesson stays as scheduled."
                    : $"The proposed new time was declined: {request.ResponseNote}",
                "lesson", $"/lesson/{lesson.Id}", $"reschedule-declined:{request.Id}"));
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [request.RequestedById], "Reschedule declined",
                "The proposed new time was declined.", $"/lesson/{lesson.Id}", ct);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            return Results.Ok(ToReschedule(request, "User", DateTimeOffset.UtcNow));
        });

        api.MapPost("/reschedule-requests/{requestId:guid}/cancel", async (
            Guid requestId, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var request = await db.LessonRescheduleRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct);
            if (request is null) return Results.NotFound();
            if (request.Status != "pending")
                return Results.Conflict(new { message = "This reschedule request is no longer open." });
            if (request.RequestedById != UserId(p) && !IsAdmin(p)) return Results.Forbid();
            request.Status = "cancelled";
            request.RespondedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ------------------------------------------------------------- make-up
        api.MapPost("/lessons/{id:guid}/replacement", async (
            Guid id, CreateReplacementRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyEmailSender email, AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var original = await LoadLessonAsync(db, id, ct);
            if (original is null) return Results.NotFound();
            if (!IsAdmin(p) && !(p.IsInRole("teacher") && original.TeacherId == UserId(p)))
                return Results.Forbid();
            if (original.Status != "cancelled")
                return Results.Conflict(new { message = "A replacement can only be created for a cancelled lesson." });
            if (await db.Lessons.AnyAsync(x => x.OriginalLessonId == id && x.Status != "cancelled", ct))
                return Results.Conflict(new { message = "This cancelled lesson already has an active replacement." });

            var now = DateTimeOffset.UtcNow;
            var startsAt = input.StartsAt.ToUniversalTime();
            var endsAt = input.EndsAt.ToUniversalTime();
            if (startsAt <= now)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startsAt"] = ["Choose a future time."] });
            if (endsAt <= startsAt)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = ["The lesson must end after it starts."] });
            var expected = LessonPolicyService.ScheduledMinutes(original);
            if (Math.Abs((int)Math.Round((endsAt - startsAt).TotalMinutes) - expected) > 1)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = [$"Keep the original {expected}-minute length."] });
            if (await HasConflictAsync(db, Guid.Empty, original.TeacherId, original.StudentId, startsAt, endsAt, ct))
                return Results.Conflict(new { message = "The teacher or student already has a lesson at that time." });

            var replacement = new Lesson
            {
                TeacherId = original.TeacherId,
                StudentId = original.StudentId,
                StartsAt = startsAt,
                EndsAt = endsAt,
                Status = "scheduled",
                IsMakeup = true,
                OriginalLessonId = original.Id,
                Teacher = original.Teacher,
                Student = original.Student,
            };
            db.Lessons.Add(replacement);

            var body = $"A make-up lesson was added for {LessonOpsMessaging.When(startsAt, original.Series?.TimeZoneId)}.";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                original.StudentId, "Make-up lesson scheduled", body, "lesson",
                $"/lesson/{replacement.Id}", $"replacement:{replacement.Id}"));
            Audit(db, UserId(p), "replacement_created", "Lesson", replacement.Id,
                $"Make-up for cancelled lesson {original.Id} at {startsAt:u}.");
            await db.SaveChangesAsync(ct);

            await LessonOpsMessaging.EmailAsync(email, original.Student, "Make-up lesson scheduled", body, ct);
            await LessonOpsMessaging.PushAsync(db, push, [original.StudentId], "Make-up lesson scheduled", body, $"/lesson/{replacement.Id}", ct);
            await realtime.ToParticipantsAsync(original.TeacherId, original.StudentId, RealtimeScopes.Lessons, replacement.Id);
            return Results.Ok(ApiEndpoints.ToLesson(replacement));
        });

        // ----------------------------------------------------------- attendance
        api.MapGet("/lessons/{id:guid}/attendance", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            return Results.Ok(await LessonPolicyService.BuildAttendanceSummaryAsync(
                db, lesson, DateTimeOffset.UtcNow, policy, ct));
        });

        api.MapPost("/lessons/{id:guid}/attendance/events", async (
            Guid id, AttendanceBeatRequest input, ClaimsPrincipal p, AcademyDbContext db,
            RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            var userId = UserId(p);
            if (userId != lesson.TeacherId && userId != lesson.StudentId) return Results.Forbid();
            var action = (input.Action ?? "").Trim().ToLowerInvariant();
            if (action is not ("join" or "leave"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["Action must be join or leave."] });
            var eventId = (input.EventId ?? "").Trim();
            if (eventId.Length is < 4 or > 80)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["eventId"] = ["Supply a unique event id so retries are safe."] });
            if (lesson.Status is "cancelled" or "completed" || DateTimeOffset.UtcNow >= lesson.EndsAt)
                return Results.Conflict(new { message = "The lesson room is closed." });

            var now = DateTimeOffset.UtcNow;
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var deviceId = (input.DeviceId ?? "default").Trim();
            if (deviceId.Length > 120) deviceId = deviceId[..120];

            var already = await db.LessonAttendanceEvents.AsNoTracking()
                .AnyAsync(x => x.UserId == userId && x.EventId == eventId, ct);
            if (already)
            {
                var summary = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
                var mine = summary.Participants.First(x => x.UserId == userId);
                return Results.Ok(new AttendanceBeatResultDto(true, mine.Status, mine.TotalSeconds, now, summary));
            }

            var attendance = await db.LessonAttendances
                .FirstOrDefaultAsync(x => x.LessonId == lesson.Id && x.UserId == userId, ct);
            if (attendance is null)
            {
                attendance = new LessonAttendance
                {
                    LessonId = lesson.Id,
                    UserId = userId,
                    Role = userId == lesson.TeacherId ? "teacher" : "student",
                };
                db.LessonAttendances.Add(attendance);
            }

            var intervals = await db.LessonAttendanceIntervals
                .Where(x => x.LessonId == lesson.Id && x.UserId == userId).ToListAsync(ct);
            if (action == "join")
            {
                var open = intervals.FirstOrDefault(x => x.LeftAt is null && x.DeviceId == deviceId);
                if (open is null)
                {
                    var interval = new LessonAttendanceInterval
                    {
                        LessonId = lesson.Id,
                        AttendanceId = attendance.Id,
                        UserId = userId,
                        DeviceId = deviceId,
                        JoinedAt = now,
                    };
                    intervals.Add(interval);
                    db.LessonAttendanceIntervals.Add(interval);
                }
                attendance.LastLeftAt = null;
                attendance.Status = "joined";
            }
            else
            {
                var closeAt = now < lesson.EndsAt ? now : lesson.EndsAt;
                foreach (var open in intervals.Where(x => x.LeftAt is null && x.DeviceId == deviceId))
                    open.LeftAt = closeAt < open.JoinedAt ? open.JoinedAt : closeAt;
            }

            LessonPolicyService.RecomputeAttendance(attendance, lesson, intervals, now, policy);
            db.LessonAttendanceEvents.Add(new LessonAttendanceEvent
            {
                LessonId = lesson.Id,
                UserId = userId,
                DeviceId = deviceId,
                EventId = eventId,
                Action = action,
                ClientAt = input.ClientAt?.ToUniversalTime() ?? now,
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two devices or a retry raced. The stored state is authoritative.
                db.ChangeTracker.Clear();
            }

            // The caller's own tabs are refreshed by the mutation middleware; the
            // other participant's lesson screen needs this push to show someone
            // joining or leaving the room.
            await realtime.ToUserAsync(
                userId == lesson.TeacherId ? lesson.StudentId : lesson.TeacherId,
                RealtimeScopes.Lessons, lesson.Id);

            var current = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
            var participant = current.Participants.First(x => x.UserId == userId);
            return Results.Ok(new AttendanceBeatResultDto(
                true, participant.Status, participant.TotalSeconds, now, current));
        });

        // --------------------------------------------------------------- excuse
        api.MapGet("/lessons/{id:guid}/excuse", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();
            var excuse = await db.LessonExcuseRequests.AsNoTracking().Include(x => x.Student).Include(x => x.Lesson)
                .Where(x => x.LessonId == id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            return excuse is null ? Results.NotFound() : Results.Ok(ToExcuse(excuse));
        });

        api.MapPost("/lessons/{id:guid}/excuse", async (
            Guid id, CreateExcuseRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyEmailSender email, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (lesson.StudentId != UserId(p)) return Results.Forbid();
            if (lesson.CancellationOutcome is not ("late_cancellation" or "student_no_show" or "both_absent"))
                return Results.Conflict(new { message = "An excuse can only be requested for a late cancellation or no-show." });
            var category = (input.Category ?? "other").Trim().ToLowerInvariant();
            if (category is not ("illness" or "family_emergency" or "technical" or "other"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Choose illness, family_emergency, technical, or other."] });
            var explanation = (input.Explanation ?? "").Trim();
            if (explanation.Length is < 10 or > 2000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["explanation"] = ["Explain what happened in 10 to 2000 characters."] });

            if (await db.LessonExcuseRequests.AnyAsync(x => x.LessonId == id && x.Status == "pending", ct))
                return Results.Conflict(new { message = "An excuse request for this lesson is already waiting for review." });
            if (await db.LessonExcuseRequests.AnyAsync(x => x.LessonId == id && x.Status == "approved", ct))
                return Results.Conflict(new { message = "This lesson was already excused." });

            var excuse = new LessonExcuseRequest
            {
                LessonId = id,
                StudentId = lesson.StudentId,
                Category = category,
                Explanation = explanation,
            };
            db.LessonExcuseRequests.Add(excuse);

            var admins = await LessonOpsMessaging.AdminUserIdsAsync(db, ct);
            var excuseLink = LessonOpsMessaging.LessonLink(lesson);
            foreach (var adminId in admins)
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    adminId, "Excuse request",
                    LessonOpsMessaging.LessonSummary(
                        lesson, $"{lesson.Student?.FullName ?? "A student"} asked to excuse this lesson: {explanation}"),
                    "lesson", excuseLink, $"excuse-admin:{excuse.Id}:{adminId}"));
            Audit(db, UserId(p), "excuse_requested", "Lesson", id, explanation);
            await db.SaveChangesAsync(ct);

            await LessonOpsMessaging.EmailAdminsAsync(db, email, "Student excuse request",
                $"{lesson.Student?.FullName ?? "A student"} requested an excuse for the lesson on {lesson.StartsAt:u}.\n\n{explanation}", ct);
            return Results.Ok(ToExcuse(new LessonExcuseRequest
            {
                Id = excuse.Id,
                LessonId = id,
                StudentId = lesson.StudentId,
                Student = lesson.Student,
                Lesson = lesson,
                Category = category,
                Explanation = explanation,
                Status = excuse.Status,
                CreatedAt = excuse.CreatedAt,
            }));
        }).RequireAuthorization("StudentOnly");

        api.MapPost("/lessons/{id:guid}/incident", async (
            Guid id, ReportIncidentRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (lesson.TeacherId != UserId(p) && lesson.StudentId != UserId(p)) return Results.Forbid();
            var type = (input.Type ?? "attendance_dispute").Trim().ToLowerInvariant();
            if (type is not ("attendance_dispute" or "potential_teacher_no_show" or "potential_student_no_show"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Unknown incident type."] });
            var detail = (input.Detail ?? "").Trim();
            if (detail.Length is < 5 or > 1000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["detail"] = ["Describe the issue in 5 to 1000 characters."] });

            if (await db.LessonIncidents.AnyAsync(x => x.LessonId == id && x.Type == type, ct))
                return Results.Conflict(new { message = "This issue was already reported for the lesson." });

            var incident = new LessonIncident
            {
                LessonId = id,
                Type = type,
                Detail = detail,
                ReportedBy = UserId(p),
            };
            db.LessonIncidents.Add(incident);
            var admins = await LessonOpsMessaging.AdminUserIdsAsync(db, ct);
            var reporter = lesson.StudentId == UserId(p) ? lesson.Student : lesson.Teacher;
            var body = LessonOpsMessaging.LessonSummary(
                lesson, $"{reporter?.FullName ?? "A participant"} reported: {detail}");
            var link = LessonOpsMessaging.LessonLink(lesson);
            foreach (var adminId in admins)
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    adminId, "Lesson attendance issue", body, "lesson", link,
                    $"incident-admin:{incident.Id}:{adminId}"));
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, admins, "Lesson attendance issue", body, link, ct);
            return Results.Ok(ToIncident(incident, lesson));
        });

        // ------------------------------------------------------------ lesson call
        api.MapPost("/lessons/{id:guid}/call", async (
            Guid id, CallLessonRequest? input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, id, ct);
            if (lesson is null) return Results.NotFound();
            if (!CanView(lesson, p)) return Results.Forbid();

            var now = DateTimeOffset.UtcNow;
            if (lesson.Status == "cancelled")
                return Results.Conflict(new { message = "This lesson was cancelled." });
            if (lesson.EndsAt <= now)
                return Results.Conflict(new { message = "The lesson time has already passed." });
            if (now < lesson.StartsAt - CallLead)
                return Results.Conflict(new { message = "Calls open shortly before the lesson starts." });

            var targetRole = (input?.TargetRole ?? "").Trim().ToLowerInvariant();
            if (targetRole.Length > 0 && targetRole is not ("teacher" or "student"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetRole"] = ["Target must be teacher or student."] });

            var callerId = UserId(p);
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var attendance = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
            AttendanceParticipantDto ByRole(string role) => attendance.Participants.First(x => x.Role == role);

            var staffOnly = callerId != lesson.TeacherId && callerId != lesson.StudentId;
            AttendanceParticipantDto target =
                staffOnly ? (targetRole.Length > 0
                    ? ByRole(targetRole)
                    : attendance.Participants.FirstOrDefault(x => x.Status != "joined") ?? ByRole("student"))
                : ByRole(callerId == lesson.TeacherId ? "student" : "teacher");

            if (target.Status == "joined")
                return Results.Conflict(new { message = $"{target.Name} is already in the lesson room." });

            var callerRole = ActorRole(p);
            var origin = staffOnly ? "monitor" : "room";
            if (await db.LessonCalls.AnyAsync(
                    x => x.LessonId == id && x.CallerId == callerId && x.CreatedAt > now - CallCooldown, ct))
                return Results.Conflict(new { message = "You just called. Wait a moment before calling again." });
            if (await db.LessonCalls.CountAsync(x => x.LessonId == id && x.CallerId == callerId, ct) >= CallsPerLesson)
                return Results.Conflict(new { message = $"You have used the {CallsPerLesson} calls allowed for this lesson." });

            var call = new LessonCall
            {
                LessonId = id,
                CallerId = callerId,
                CallerRole = callerRole,
                CalleeId = target.UserId,
                CalleeRole = target.Role,
                Origin = origin,
                CalleeStatus = target.Status,
                CreatedAt = now,
            };
            db.LessonCalls.Add(call);

            var callerName = callerId == lesson.TeacherId ? lesson.Teacher?.FullName
                : callerId == lesson.StudentId ? lesson.Student?.FullName
                : (await db.Users.AsNoTracking().Where(x => x.Id == callerId).Select(x => x.FullName).FirstOrDefaultAsync(ct));
            var detail = callerRole == "student" ? "is calling you. Open the lesson room."
                : callerRole == "teacher" ? "is calling you. Join the lesson now."
                : "is calling you from the academy. Join the lesson now.";
            var body = LessonOpsMessaging.LessonSummary(lesson, $"{callerName ?? "The other participant"} {detail}");
            var title = callerRole switch
            {
                "student" => "Call from your student",
                "teacher" => "Call from your teacher",
                _ => "The academy is calling you",
            };
            var link = LessonOpsMessaging.LessonLink(lesson);
            db.Notifications.Add(LessonOpsMessaging.Notice(
                target.UserId, title, body, "lesson", link, $"call:{call.Id}"));
            Audit(db, callerId, "lesson_called", "Lesson", id,
                $"{callerName ?? "A user"} ({callerRole}) called {target.Name} ({target.Role}). Status: {target.Status}.");
            await db.SaveChangesAsync(ct);

            await LessonOpsMessaging.PushAsync(
                db, push, [target.UserId], title, body, link, ct, LessonOpsMessaging.UrgentCall);
            await realtime.ToUsersAsync([callerId, target.UserId], RealtimeScopes.Calls, id);
            return Results.Ok(ToCall(call));
        });

        // Calls still worth ringing for: the client asks on launch and on focus,
        // because a push can be missed while another app owns the screen.
        api.MapGet("/calls/recent", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
            var items = await db.LessonCalls.AsNoTracking()
                .Include(x => x.Caller).Include(x => x.Callee)
                .Where(x => x.CalleeId == UserId(p) && x.CreatedAt >= cutoff)
                .OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct);
            return Results.Ok(items.Select(ToCall));
        });

        // ------------------------------------------------------------ my records
        api.MapGet("/student/credits", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var studentId = UserId(p);
            var items = await db.LessonCreditTransactions.AsNoTracking()
                .Where(x => x.StudentId == studentId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(new StudentCreditDto(studentId, items.Sum(x => x.Amount), items.Select(ToCredit).ToList()));
        }).RequireAuthorization("StudentOnly");

        api.MapGet("/student/reliability", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var studentId = UserId(p);
            var items = await db.ReliabilityEvents.AsNoTracking().Include(x => x.User)
                .Where(x => x.UserId == studentId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(items.Select(ToReliability));
        }).RequireAuthorization("StudentOnly");

        api.MapGet("/teacher/reliability", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var teacherId = UserId(p);
            var items = await db.ReliabilityEvents.AsNoTracking().Include(x => x.User)
                .Where(x => x.UserId == teacherId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(items.Select(ToReliability));
        }).RequireAuthorization("TeacherOnly");

        api.MapGet("/teacher/hours", async (string? month, string? status, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var (start, end, _) = LessonPolicyService.MonthBounds(month, now);
            var q = db.TeacherHours.AsNoTracking()
                .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .Where(x => x.TeacherId == UserId(p) && x.Lesson != null
                    && x.Lesson.StartsAt >= start && x.Lesson.StartsAt < end);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.ApprovalStatus == status);
            var items = await q.OrderByDescending(x => x.Lesson!.StartsAt).Take(300).ToListAsync(ct);
            return Results.Ok(items.Select(x => ToHours(x, x.Lesson!)));
        }).RequireAuthorization("TeacherOnly");

        api.MapGet("/teacher/salary-periods", async (ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var items = await db.SalaryPeriods.AsNoTracking().Include(x => x.Teacher)
                .Where(x => x.TeacherId == UserId(p)).OrderByDescending(x => x.Month).Take(24).ToListAsync(ct);
            return Results.Ok(items.Select(ToSalary));
        }).RequireAuthorization("TeacherOnly");

        // ----------------------------------------------------------- push tokens
        api.MapPost("/devices/register", async (RegisterDeviceTokenRequest input, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var token = (input.Token ?? "").Trim();
            if (token.Length is < 10 or > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["Supply the Expo push token from the device."] });
            var platform = (input.Platform ?? "android").Trim().ToLowerInvariant();
            if (platform is not ("ios" or "android")) platform = "android";
            var now = DateTimeOffset.UtcNow;
            var existing = await db.DeviceTokens.FirstOrDefaultAsync(x => x.Token == token, ct);
            if (existing is null)
                db.DeviceTokens.Add(new DeviceToken { UserId = UserId(p), Token = token, Platform = platform });
            else
            {
                existing.UserId = UserId(p);
                existing.Platform = platform;
                existing.LastSeenAt = now;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapPost("/devices/unregister", async (RegisterDeviceTokenRequest input, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var token = (input.Token ?? "").Trim();
            if (token.Length == 0) return Results.NoContent();
            await db.DeviceTokens.Where(x => x.Token == token && x.UserId == UserId(p))
                .ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        MapAdminOperations(app);
    }

    /// <summary>
    /// Shared by the dedicated cancel endpoint and the legacy DELETE route so
    /// neither path can bypass the policy, the ledger, or the notifications.
    /// </summary>
    internal static async Task<IResult> CancelAsync(
        Guid id,
        string? reasonInput,
        ClaimsPrincipal p,
        AcademyDbContext db,
        AcademyEmailSender email,
        AcademyPushSender push,
        RealtimeNotifier realtime,
        CancellationToken ct)
    {
        var lesson = await LoadLessonAsync(db, id, ct);
        if (lesson is null) return Results.NotFound();
        var role = ActorRole(p);
        if (role == "support" || (!IsAdmin(p) && lesson.TeacherId != UserId(p) && lesson.StudentId != UserId(p)))
            return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
        var decision = await LessonPolicyService.EvaluateCancellationAsync(db, lesson, role, now, policy, ct);
        if (!decision.Allowed)
            return Results.Conflict(new { message = decision.BlockedReason, preview = decision });

        var reason = (reasonInput ?? "").Trim();
        if (reason.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Keep the reason under 1000 characters."] });

        LessonPolicyService.ApplyCancellation(db, lesson, decision, UserId(p), reason, now);
        var openReschedules = await db.LessonRescheduleRequests
            .Where(x => x.LessonId == lesson.Id && x.Status == "pending").ToListAsync(ct);
        foreach (var open in openReschedules)
        {
            open.Status = "cancelled";
            open.RespondedAt = now;
        }
        await LessonPolicyService.CloseAttendanceAsync(db, lesson, now, policy, ct);
        await LessonPolicyService.EnsureTeacherHoursAsync(
            db, lesson, policy, now, decision.TeacherPayableMinutes, ct);

        await db.SaveChangesAsync(ct);

        LessonPolicyService.ReliabilityOutcome? reliability = null;
        if (decision.ReliabilityEvent is not null)
        {
            reliability = await LessonPolicyService.AddReliabilityEventAsync(
                db, role == "teacher" ? lesson.TeacherId : lesson.StudentId, role,
                lesson.Id, decision.ReliabilityEvent, reason, UserId(p), policy, now, ct);
            await db.SaveChangesAsync(ct);
        }

        // Every notice below names whoever actually cancelled, so a student never
        // reads their own name in "… cancelled your lesson".
        var actorName = role switch
        {
            "teacher" => lesson.Teacher?.FullName ?? "Your teacher",
            "student" => lesson.Student?.FullName ?? "Your student",
            _ => "The academy",
        };
        var when = LessonOpsMessaging.When(lesson.StartsAt, lesson.Series?.TimeZoneId);
        var (title, body) = role switch
        {
            "student" => ("Lesson cancelled", $"{actorName} cancelled the lesson on {when}."),
            "teacher" => ("Lesson cancelled", $"{actorName} cancelled your lesson on {when}."),
            _ => ("Lesson cancelled by the academy", $"The lesson on {when} was cancelled. Your teacher/student has been notified."),
        };

        var notifyIds = new List<Guid>();
        if (role == "student")
        {
            notifyIds.Add(lesson.TeacherId);
            db.Notifications.Add(LessonOpsMessaging.Notice(
                lesson.TeacherId, title, body, "lesson", $"/lesson/{lesson.Id}", $"cancel-teacher:{lesson.Id}"));
            await LessonOpsMessaging.EmailAsync(email, lesson.Teacher,
                "Lesson cancelled by student", body, ct);
        }
        else if (role == "teacher")
        {
            notifyIds.Add(lesson.StudentId);
            db.Notifications.Add(LessonOpsMessaging.Notice(
                lesson.StudentId, title, body, "lesson", $"/lesson/{lesson.Id}", $"cancel-student:{lesson.Id}"));
            await LessonOpsMessaging.EmailAsync(email, lesson.Student,
                "Your lesson was cancelled", body, ct);
        }
        else
        {
            notifyIds.AddRange([lesson.TeacherId, lesson.StudentId]);
            db.Notifications.AddRange(
                LessonOpsMessaging.Notice(lesson.TeacherId, title, body, "lesson", $"/lesson/{lesson.Id}", $"cancel-teacher:{lesson.Id}"),
                LessonOpsMessaging.Notice(lesson.StudentId, title, body, "lesson", $"/lesson/{lesson.Id}", $"cancel-student:{lesson.Id}"));
            await LessonOpsMessaging.EmailAsync(email, lesson.Teacher, "Lesson cancelled by the academy", body, ct);
            await LessonOpsMessaging.EmailAsync(email, lesson.Student, "Lesson cancelled by the academy", body, ct);
        }

        if (reliability?.WarningIssued == true)
            db.Notifications.Add(LessonOpsMessaging.Notice(
                lesson.StudentId, "Reliability warning",
                "You have reached the monthly cancellation warning threshold. Contact the academy before cancelling again.",
                "system", "/schedule", $"reliability-warning:{lesson.Id}"));

        if (decision.RequiresAdminReview || reliability?.AdminReviewRequired == true)
        {
            var admins = await LessonOpsMessaging.AdminUserIdsAsync(db, ct);
            var reviewBody = LessonOpsMessaging.LessonSummary(
                lesson, $"{actorName} cancelled {decision.HoursBefore:0.#} hours before it started. Review reliability and compensation.");
            var reviewLink = LessonOpsMessaging.LessonLink(lesson);
            foreach (var adminId in admins)
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    adminId, "Cancellation needs review", reviewBody, "lesson", reviewLink,
                    $"cancel-review:{lesson.Id}:{adminId}"));
            await LessonOpsMessaging.EmailAdminsAsync(db, email,
                "Late cancellation needs review", body, ct);
        }

        await db.SaveChangesAsync(ct);
        await LessonOpsMessaging.PushAsync(db, push, notifyIds, title, body, $"/lesson/{lesson.Id}", ct);
        await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);

        return Results.Ok(new { preview = decision, lesson = ApiEndpoints.ToLesson(lesson) });
    }

    private static void MapAdminOperations(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

        // ------------------------------------------------------- live dashboard
        admin.MapGet("/lesson-ops", async (string? date, AcademyDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var (dayStart, dayEnd, key) = LessonPolicyService.DayBounds(date, now);
            var lessons = await db.Lessons.AsNoTracking()
                .Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review)
                .Where(x => x.StartsAt >= dayStart && x.StartsAt < dayEnd)
                .OrderBy(x => x.StartsAt).Take(200).ToListAsync(ct);
            var ids = lessons.Select(x => x.Id).ToList();
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);

            var pendingReschedules = await db.LessonRescheduleRequests.AsNoTracking()
                .Where(x => ids.Contains(x.LessonId) && x.Status == "pending" && x.ExpiresAt > now)
                .ToListAsync(ct);
            var requesterNames = await NamesAsync(db, pendingReschedules.Select(x => x.RequestedById), ct);
            var hours = await db.TeacherHours.AsNoTracking()
                .Where(x => ids.Contains(x.LessonId)).ToDictionaryAsync(x => x.LessonId, ct);
            var incidents = await db.LessonIncidents.AsNoTracking()
                .Where(x => ids.Contains(x.LessonId)).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

            var rows = new List<LessonOpsLessonDto>();
            var pendingReports = 0;
            var missing = 0;
            foreach (var lesson in lessons)
            {
                var attendance = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
                var reportDue = lesson.Status is not ("cancelled" or "completed") && lesson.EndsAt <= now && lesson.Report is null;
                if (reportDue) pendingReports++;
                if (lesson.EndsAt <= now && attendance.Participants.All(x => x.TotalSeconds == 0)) missing++;
                var pending = pendingReschedules.FirstOrDefault(x => x.LessonId == lesson.Id);
                rows.Add(new LessonOpsLessonDto(
                    ApiEndpoints.ToLesson(lesson),
                    attendance,
                    lesson.CancellationOutcome,
                    pending is null ? null : ToReschedule(pending, requesterNames.GetValueOrDefault(pending.RequestedById, "User"), now),
                    hours.TryGetValue(lesson.Id, out var h) ? ToHours(h, lesson) : null,
                    incidents.Where(x => x.LessonId == lesson.Id).Select(x => ToIncident(x, lesson)).ToList(),
                    reportDue));
            }

            var summary = new LessonOpsSummaryDto(
                lessons.Count,
                lessons.Count(x => x.Status == "scheduled" && x.StartsAt <= now && x.EndsAt > now),
                lessons.Count(x => x.Status == "scheduled" && x.StartsAt > now),
                pendingReports,
                missing,
                await db.LessonRescheduleRequests.CountAsync(x => x.Status == "pending" && x.ExpiresAt > now, ct),
                await db.LessonExcuseRequests.CountAsync(x => x.Status == "pending", ct),
                await db.LessonIncidents.CountAsync(x => x.Status == "open", ct),
                await db.TeacherHours.CountAsync(x => x.ApprovalStatus == "pending" && x.Lesson != null && x.Lesson.EndsAt <= now, ct));

            return Results.Ok(new AdminLessonOpsDto(key, summary, rows));
        });

        admin.MapGet("/lesson-ops/{lessonId:guid}", async (Guid lessonId, AcademyDbContext db, CancellationToken ct) =>
        {
            var lesson = await LoadLessonAsync(db, lessonId, ct);
            if (lesson is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            var attendance = await LessonPolicyService.BuildAttendanceSummaryAsync(db, lesson, now, policy, ct);
            var intervals = await db.LessonAttendanceIntervals.AsNoTracking()
                .Where(x => x.LessonId == lessonId).OrderBy(x => x.JoinedAt).ToListAsync(ct);
            var cancellation = await db.LessonCancellations.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LessonId == lessonId, ct);
            var requests = await db.LessonRescheduleRequests.AsNoTracking()
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var names = await NamesAsync(db, requests.Select(x => x.RequestedById), ct);
            var hours = await db.TeacherHours.AsNoTracking().FirstOrDefaultAsync(x => x.LessonId == lessonId, ct);
            var incidents = await db.LessonIncidents.AsNoTracking()
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var excuse = await db.LessonExcuseRequests.AsNoTracking().Include(x => x.Student).Include(x => x.Lesson)
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var credits = await db.LessonCreditTransactions.AsNoTracking()
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var changes = await db.LessonScheduleChanges.AsNoTracking()
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var calls = await db.LessonCalls.AsNoTracking()
                .Include(x => x.Caller).Include(x => x.Callee)
                .Where(x => x.LessonId == lessonId).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct);

            return Results.Ok(new AdminLessonOpsDetailDto(
                ApiEndpoints.ToLesson(lesson), attendance,
                intervals.Select(x => new AttendanceIntervalDto(
                    x.Id, x.DeviceId, x.JoinedAt, x.LeftAt,
                    LessonPolicyService.OverlapSeconds(x.JoinedAt, x.LeftAt, lesson.StartsAt, lesson.EndsAt, now))).ToList(),
                cancellation is null ? null : ToCancellation(cancellation),
                requests.Select(x => ToReschedule(x, names.GetValueOrDefault(x.RequestedById, "User"), now)).ToList(),
                hours is null ? null : ToHours(hours, lesson),
                incidents.Select(x => ToIncident(x, lesson)).ToList(),
                excuse is null ? null : ToExcuse(excuse),
                credits.Select(ToCredit).ToList(),
                changes.Select(ToScheduleChange).ToList(),
                calls.Select(ToCall).ToList()));
        });

        // ------------------------------------------------------------- excuses
        admin.MapGet("/excuse-requests", async (string? status, AcademyDbContext db, CancellationToken ct) =>
        {
            var q = db.LessonExcuseRequests.AsNoTracking()
                .Include(x => x.Student).Include(x => x.Lesson).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            var items = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(items.Select(ToExcuse));
        });

        admin.MapPost("/excuse-requests/{id:guid}/approve", async (
            Guid id, ExcuseReviewRequest? input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, AcademyEmailSender email, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var excuse = await db.LessonExcuseRequests
                .Include(x => x.Student).Include(x => x.Lesson)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (excuse?.Lesson is null) return Results.NotFound();
            if (excuse.Status != "pending")
                return Results.Conflict(new { message = "This excuse request was already reviewed." });

            var lesson = excuse.Lesson;
            var now = DateTimeOffset.UtcNow;
            excuse.Status = "approved";
            excuse.ReviewedBy = UserId(p);
            excuse.ReviewedAt = now;
            excuse.AdminNote = (input?.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 1000)] : null;

            if (!lesson.IsTrial)
                LessonPolicyService.AddCredit(db, lesson.StudentId, lesson.Id, "excuse_approved", 1,
                    "Late cancellation or no-show excused by the academy", UserId(p), "admin",
                    $"excuse:{lesson.Id}");

            var reliability = await db.ReliabilityEvents
                .Where(x => x.LessonId == lesson.Id && x.UserId == lesson.StudentId && x.ResolvedAt == null).ToListAsync(ct);
            foreach (var item in reliability)
            {
                item.ResolvedBy = UserId(p);
                item.ResolvedAt = now;
                item.ResolutionNote = excuse.AdminNote ?? "Excused by the academy.";
                item.ResolutionAction = "excused";
            }

            var body = "The academy approved your excuse. The lesson credit was restored and the related reliability note was cleared.";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                lesson.StudentId, "Excuse approved", body, "system", $"/lesson/{lesson.Id}", $"excuse-approved:{lesson.Id}"));
            Audit(db, UserId(p), "excuse_approved", "Lesson", lesson.Id, excuse.Explanation);
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [lesson.StudentId], "Excuse approved", body, $"/lesson/{lesson.Id}", ct);
            await LessonOpsMessaging.EmailAsync(email, lesson.Student, "Excuse approved", body, ct);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            return Results.Ok(ToExcuse(excuse));
        });

        admin.MapPost("/excuse-requests/{id:guid}/reject", async (
            Guid id, ExcuseReviewRequest? input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var excuse = await db.LessonExcuseRequests.Include(x => x.Student).Include(x => x.Lesson)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (excuse?.Lesson is null) return Results.NotFound();
            if (excuse.Status != "pending")
                return Results.Conflict(new { message = "This excuse request was already reviewed." });
            excuse.Status = "rejected";
            excuse.ReviewedBy = UserId(p);
            excuse.ReviewedAt = DateTimeOffset.UtcNow;
            excuse.AdminNote = (input?.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 1000)] : null;
            var body = excuse.AdminNote is null
                ? "The academy reviewed your excuse but could not approve it."
                : $"The academy reviewed your excuse but could not approve it: {excuse.AdminNote}";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                excuse.Lesson.StudentId, "Excuse not approved", body, "system",
                $"/lesson/{excuse.LessonId}", $"excuse-rejected:{excuse.LessonId}"));
            Audit(db, UserId(p), "excuse_rejected", "Lesson", excuse.LessonId, excuse.AdminNote ?? "");
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [excuse.Lesson.StudentId], "Excuse not approved", body, $"/lesson/{excuse.LessonId}", ct);
            await realtime.ToParticipantsAsync(excuse.Lesson.TeacherId, excuse.Lesson.StudentId, RealtimeScopes.Lessons, excuse.LessonId);
            return Results.Ok(ToExcuse(excuse));
        });

        // ------------------------------------------------------------ incidents
        admin.MapGet("/lesson-incidents", async (string? status, AcademyDbContext db, CancellationToken ct) =>
        {
            var q = db.LessonIncidents.AsNoTracking()
                .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            var items = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(items.Where(x => x.Lesson is not null).Select(x => ToIncident(x, x.Lesson!)));
        });

        admin.MapPost("/lesson-incidents/{id:guid}/resolve", async (
            Guid id, ResolveIncidentRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, AcademyEmailSender email, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var incident = await db.LessonIncidents
                .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (incident?.Lesson is null) return Results.NotFound();
            if (incident.Status != "open")
                return Results.Conflict(new { message = "This incident was already resolved." });
            var action = (input.Action ?? "").Trim().ToLowerInvariant();
            if (action is not ("student_no_show" or "teacher_no_show" or "both_absent" or "dismissed"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["Choose student_no_show, teacher_no_show, both_absent, or dismissed."] });

            var lesson = incident.Lesson;
            var now = DateTimeOffset.UtcNow;
            var policy = await LessonPolicyService.GetPolicyAsync(db, ct);
            incident.Status = action == "dismissed" ? "dismissed" : "resolved";
            incident.ResolvedBy = UserId(p);
            incident.ResolvedAt = now;
            incident.ResolutionNote = (input.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 1000)] : null;

            if (action != "dismissed")
            {
                var scheduled = LessonPolicyService.ScheduledMinutes(lesson);
                var payable = Math.Clamp(input.TeacherPayableMinutes ?? (action == "student_no_show" ? scheduled : 0), 0, scheduled);
                if (lesson.Status != "cancelled")
                {
                    lesson.Status = "cancelled";
                    lesson.CancellationOutcome = action;
                    lesson.CancelReason = incident.ResolutionNote ?? $"Resolved as {action.Replace('_', ' ')}";
                    lesson.CancelledById = UserId(p);
                    lesson.CancelledAt = now;
                    var openReschedules = await db.LessonRescheduleRequests
                        .Where(x => x.LessonId == lesson.Id && x.Status == "pending").ToListAsync(ct);
                    foreach (var open in openReschedules)
                    {
                        open.Status = "cancelled";
                        open.RespondedAt = now;
                    }
                    db.LessonCancellations.Add(new LessonCancellation
                    {
                        LessonId = lesson.Id,
                        CancelledById = UserId(p),
                        ActorRole = "admin",
                        Outcome = action,
                        IsLate = true,
                        TeacherPayableMinutes = payable,
                        Reason = lesson.CancelReason,
                        PolicySnapshot = System.Text.Json.JsonSerializer.Serialize(LessonPolicyService.ToDto(policy)),
                        CreatedAt = now,
                    });
                    await LessonPolicyService.CloseAttendanceAsync(db, lesson, now, policy, ct);
                }

                var studentBody = action switch
                {
                    "student_no_show" => "The academy recorded a no-show for this lesson. One lesson credit was used.",
                    "teacher_no_show" => "The academy recorded that the teacher did not attend. Your lesson credit was restored.",
                    "both_absent" => "The academy recorded that neither participant joined this lesson. No compensation was generated.",
                    _ => "The academy resolved the attendance issue.",
                };
                var teacherBody = action switch
                {
                    "student_no_show" => "The academy confirmed the student did not attend. Compensation for the scheduled time was recorded.",
                    "teacher_no_show" => "The academy confirmed you did not join the lesson. No compensation was generated.",
                    "both_absent" => "The academy confirmed neither participant joined. No compensation was generated.",
                    _ => "The academy resolved the attendance issue.",
                };
                db.Notifications.AddRange(
                    LessonOpsMessaging.Notice(lesson.StudentId, "Attendance issue resolved", studentBody, "lesson",
                        $"/lesson/{lesson.Id}", $"incident-student:{incident.Id}"),
                    LessonOpsMessaging.Notice(lesson.TeacherId, "Attendance issue resolved", teacherBody, "lesson",
                        $"/lesson/{lesson.Id}", $"incident-teacher:{incident.Id}"));

                if (action == "student_no_show")
                {
                    if (!lesson.IsTrial)
                        LessonPolicyService.AddCredit(db, lesson.StudentId, lesson.Id, "student_no_show", -1,
                            "Student no-show confirmed by the academy", UserId(p), "admin", $"no-show:{lesson.Id}");
                    await LessonPolicyService.AddReliabilityEventAsync(db, lesson.StudentId, "student",
                        lesson.Id, "student_no_show", incident.ResolutionNote, UserId(p), policy, now, ct);
                    var trialNoShows = await LessonPolicyService.CountTrialStudentNoShowsAsync(db, lesson.StudentId, ct);
                    if (lesson.IsTrial && trialNoShows >= policy.TrialNoShowThreshold)
                        await LessonOpsMessaging.EmailAdminsAsync(db, email, "Trial student no-show threshold reached",
                            $"{lesson.Student?.FullName ?? "A student"} reached {trialNoShows} trial no-shows.", ct);
                }
                else if (action == "teacher_no_show")
                {
                    if (!lesson.IsTrial)
                        LessonPolicyService.AddCredit(db, lesson.StudentId, lesson.Id, "teacher_no_show", 1,
                            "Teacher no-show confirmed by the academy", UserId(p), "admin", $"teacher-no-show:{lesson.Id}");
                    await LessonPolicyService.AddReliabilityEventAsync(db, lesson.TeacherId, "teacher",
                        lesson.Id, "teacher_no_show", incident.ResolutionNote, UserId(p), policy, now, ct);
                }
                else if (action == "both_absent")
                {
                    await LessonPolicyService.AddReliabilityEventAsync(db, lesson.StudentId, "student",
                        lesson.Id, "student_no_show", incident.ResolutionNote, UserId(p), policy, now, ct);
                    await LessonPolicyService.AddReliabilityEventAsync(db, lesson.TeacherId, "teacher",
                        lesson.Id, "teacher_no_show", incident.ResolutionNote, UserId(p), policy, now, ct);
                }

                await LessonPolicyService.EnsureTeacherHoursAsync(db, lesson, policy, now, payable, ct);
                Audit(db, UserId(p), "incident_resolved", "Lesson", lesson.Id,
                    $"{action}. {incident.ResolutionNote}".Trim());
            }
            else
            {
                db.Notifications.AddRange(
                    LessonOpsMessaging.Notice(lesson.StudentId, "Attendance issue dismissed",
                        "The academy reviewed the attendance issue and took no action.", "lesson",
                        $"/lesson/{lesson.Id}", $"incident-student:{incident.Id}"),
                    LessonOpsMessaging.Notice(lesson.TeacherId, "Attendance issue dismissed",
                        "The academy reviewed the attendance issue and took no action.", "lesson",
                        $"/lesson/{lesson.Id}", $"incident-teacher:{incident.Id}"));
            }

            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [lesson.StudentId, lesson.TeacherId],
                "Attendance issue resolved", "The academy resolved a lesson attendance issue.", $"/lesson/{lesson.Id}", ct);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            return Results.Ok(ToIncident(incident, lesson));
        });

        // --------------------------------------------------------------- hours
        admin.MapGet("/teacher-hours", async (string? month, string? status, AcademyDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var (start, end, _) = LessonPolicyService.MonthBounds(month, now);
            var q = db.TeacherHours.AsNoTracking()
                .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .Where(x => x.Lesson != null && x.Lesson.StartsAt >= start && x.Lesson.StartsAt < end);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.ApprovalStatus == status);
            var items = await q.OrderByDescending(x => x.Lesson!.StartsAt).Take(500).ToListAsync(ct);
            return Results.Ok(items.Select(x => ToHours(x, x.Lesson!)));
        });

        admin.MapPost("/teacher-hours/{id:guid}/approve", async (
            Guid id, ApproveTeacherHoursRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var hours = await db.TeacherHours.Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .Include(x => x.Lesson).ThenInclude(x => x!.Series)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (hours?.Lesson is null) return Results.NotFound();
            var approved = Math.Clamp(input.ApprovedMinutes ?? hours.ProposedPayableMinutes, 0, hours.ScheduledMinutes);
            hours.ApprovedPayableMinutes = approved;
            hours.ApprovalStatus = approved == hours.ProposedPayableMinutes ? "approved" : "adjusted";
            hours.ApprovedBy = UserId(p);
            hours.ApprovedAt = DateTimeOffset.UtcNow;
            hours.UpdatedAt = DateTimeOffset.UtcNow;
            hours.AdjustmentReason = (input.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            var body = $"Your payable time for the lesson on {LessonOpsMessaging.When(hours.Lesson.StartsAt, hours.Lesson.Series?.TimeZoneId)} was approved at {approved} minutes.";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                hours.TeacherId, "Teacher hours approved", body, "payment",
                $"/lesson/{hours.LessonId}", $"hours-approved:{hours.Id}"));
            Audit(db, UserId(p), "teacher_hours_approved", "TeacherHours", hours.Id,
                $"Proposed {hours.ProposedPayableMinutes}, approved {approved}. {hours.AdjustmentReason}".Trim());
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [hours.TeacherId], "Teacher hours approved", body, $"/lesson/{hours.LessonId}", ct);
            await realtime.ToUserAsync(hours.TeacherId, RealtimeScopes.Billing, hours.LessonId);
            return Results.Ok(ToHours(hours, hours.Lesson));
        });

        admin.MapPost("/teacher-hours/{id:guid}/reject", async (
            Guid id, ApproveTeacherHoursRequest input, ClaimsPrincipal p, AcademyDbContext db,
            RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var hours = await db.TeacherHours.Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
                .Include(x => x.Lesson).ThenInclude(x => x!.Student)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (hours?.Lesson is null) return Results.NotFound();
            hours.ApprovedPayableMinutes = 0;
            hours.ApprovalStatus = "rejected";
            hours.ApprovedBy = UserId(p);
            hours.ApprovedAt = DateTimeOffset.UtcNow;
            hours.UpdatedAt = DateTimeOffset.UtcNow;
            hours.AdjustmentReason = (input.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            Audit(db, UserId(p), "teacher_hours_rejected", "TeacherHours", hours.Id, hours.AdjustmentReason ?? "");
            await db.SaveChangesAsync(ct);
            await realtime.ToUserAsync(hours.TeacherId, RealtimeScopes.Billing, hours.LessonId);
            return Results.Ok(ToHours(hours, hours.Lesson));
        });

        // ------------------------------------------------------------ penalties
        admin.MapGet("/teacher-penalties", async (string? status, AcademyDbContext db, CancellationToken ct) =>
        {
            var q = db.TeacherPenalties.AsNoTracking().Include(x => x.Teacher).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            var items = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(items.Select(ToPenalty));
        });

        admin.MapPost("/teacher-penalties", async (
            CreateTeacherPenaltyRequest input, ClaimsPrincipal p, AcademyDbContext db,
            RealtimeNotifier realtime, CancellationToken ct) =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == input.TeacherId, ct)) return Results.NotFound();
            if (input.AmountEgp < 0 || input.AmountEgp > 100_000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amountEgp"] = ["Enter an amount from 0 to 100,000."] });
            var reason = (input.Reason ?? "").Trim();
            if (reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Describe the penalty in 5 to 500 characters."] });
            var eventType = (input.EventType ?? "manual").Trim().ToLowerInvariant();
            if (eventType.Length > 60) eventType = eventType[..60];
            var penalty = new TeacherPenalty
            {
                TeacherId = input.TeacherId,
                LessonId = input.LessonId,
                EventType = eventType,
                AmountEgp = input.AmountEgp,
                Reason = reason,
            };
            db.TeacherPenalties.Add(penalty);
            Audit(db, UserId(p), "penalty_created", "TeacherPenalty", penalty.Id, $"{eventType}: {reason}");
            await db.SaveChangesAsync(ct);
            await realtime.ToUserAsync(penalty.TeacherId, RealtimeScopes.Billing);
            return Results.Ok(ToPenalty(await db.TeacherPenalties.Include(x => x.Teacher)
                .FirstAsync(x => x.Id == penalty.Id, ct)));
        });

        admin.MapPost("/teacher-penalties/{id:guid}/review", async (
            Guid id, ReviewTeacherPenaltyRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var penalty = await db.TeacherPenalties.Include(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (penalty is null) return Results.NotFound();
            var status = (input.Status ?? "").Trim().ToLowerInvariant();
            if (status is not ("approved" or "rejected"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Choose approved or rejected."] });
            penalty.Status = status;
            penalty.ReviewedBy = UserId(p);
            penalty.ReviewedAt = DateTimeOffset.UtcNow;
            penalty.AdminNote = (input.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            var body = status == "approved"
                ? $"A penalty of {penalty.AmountEgp:0.##} EGP was approved: {penalty.Reason}"
                : $"A proposed penalty was rejected: {penalty.Reason}";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                penalty.TeacherId, "Penalty review", body, "payment", "/earnings", $"penalty:{penalty.Id}"));
            Audit(db, UserId(p), $"penalty_{status}", "TeacherPenalty", penalty.Id, penalty.AdminNote ?? "");
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [penalty.TeacherId], "Penalty review", body, "/earnings", ct);
            await realtime.ToUserAsync(penalty.TeacherId, RealtimeScopes.Billing);
            return Results.Ok(ToPenalty(penalty));
        });

        // ------------------------------------------------------------ reliability
        admin.MapGet("/reliability", async (string? role, Guid? userId, string? type, string? status, AcademyDbContext db, CancellationToken ct) =>
        {
            var q = db.ReliabilityEvents.AsNoTracking().Include(x => x.User).AsQueryable();
            if (!string.IsNullOrWhiteSpace(role)) q = q.Where(x => x.Role == role);
            if (userId is not null) q = q.Where(x => x.UserId == userId);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(x => x.Type == type);
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status is "open" or "pending" or "unresolved")
                    q = q.Where(x => x.ResolvedAt == null);
                else if (status == "resolved")
                    q = q.Where(x => x.ResolvedAt != null);
            }
            var items = await q.OrderByDescending(x => x.CreatedAt).Take(300).ToListAsync(ct);
            return Results.Ok(items.Select(ToReliability));
        });

        admin.MapPost("/reliability/{id:guid}/resolve", async (
            Guid id, ResolveReliabilityRequest input, ClaimsPrincipal p, AcademyDbContext db, CancellationToken ct) =>
        {
            var item = await db.ReliabilityEvents.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            item.ResolvedBy = UserId(p);
            item.ResolvedAt = DateTimeOffset.UtcNow;
            item.ResolutionNote = (input.Note ?? "").Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            item.ResolutionAction = (input.Action ?? "reviewed").Trim() is { Length: > 0 } action ? action[..Math.Min(action.Length, 50)] : "reviewed";
            Audit(db, UserId(p), "reliability_resolved", "ReliabilityEvent", item.Id, item.ResolutionNote ?? "");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToReliability(item));
        });

        // ---------------------------------------------------------------- credits
        admin.MapGet("/students/{studentId:guid}/credits", async (Guid studentId, AcademyDbContext db, CancellationToken ct) =>
        {
            var items = await db.LessonCreditTransactions.AsNoTracking()
                .Where(x => x.StudentId == studentId).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(new StudentCreditDto(studentId, items.Sum(x => x.Amount), items.Select(ToCredit).ToList()));
        });

        admin.MapPost("/students/{studentId:guid}/credits/adjust", async (
            Guid studentId, AdjustCreditRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            if (input.Amount is 0 || Math.Abs(input.Amount) > 50)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["Adjust by a non-zero amount between -50 and 50."] });
            var reason = (input.Reason ?? "").Trim();
            if (reason.Length is < 5 or > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Explain the adjustment in 5 to 500 characters."] });
            if (!await db.Users.AnyAsync(x => x.Id == studentId, ct)) return Results.NotFound();
            var credit = new LessonCreditTransaction
            {
                StudentId = studentId,
                Type = "admin_adjustment",
                Amount = input.Amount,
                Reason = reason,
                CreatedBy = UserId(p),
                CreatedByRole = "admin",
            };
            db.LessonCreditTransactions.Add(credit);
            var body = $"The academy adjusted your lesson credits by {input.Amount:+#;-#;0}: {reason}";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                studentId, "Lesson credit adjustment", body, "system", "/schedule", $"credit-adjust:{credit.Id}"));
            Audit(db, UserId(p), "credit_adjusted", "Student", studentId, body);
            await db.SaveChangesAsync(ct);
            await LessonOpsMessaging.PushAsync(db, push, [studentId], "Lesson credit adjustment", body, "/schedule", ct);
            await realtime.ToUserAsync(studentId, RealtimeScopes.Lessons);
            return Results.Ok(ToCredit(credit));
        });

        // ---------------------------------------------------------- salary periods
        admin.MapGet("/salary-periods", async (string? month, AcademyDbContext db, CancellationToken ct) =>
        {
            var q = db.SalaryPeriods.AsNoTracking().Include(x => x.Teacher).AsQueryable();
            if (!string.IsNullOrWhiteSpace(month)) q = q.Where(x => x.Month == month);
            var items = await q.OrderByDescending(x => x.Month).Take(300).ToListAsync(ct);
            return Results.Ok(items.Select(ToSalary));
        });

        admin.MapPost("/salary-periods/generate", async (GenerateSalaryPeriodsRequest? input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var (start, end, key) = LessonPolicyService.MonthBounds(input?.Month, now);
            var teacherRoleId = await db.Roles.Where(r => r.NormalizedName == "TEACHER").Select(r => r.Id).FirstOrDefaultAsync(ct);
            var teacherIds = teacherRoleId == Guid.Empty
                ? []
                : await db.UserRoles.Where(x => x.RoleId == teacherRoleId).Select(x => x.UserId).Distinct().ToListAsync(ct);

            foreach (var teacherId in teacherIds)
            {
                var period = await db.SalaryPeriods.FirstOrDefaultAsync(x => x.TeacherId == teacherId && x.Month == key, ct);
                var locked = period is not null && period.Status is "reviewed" or "paid";
                if (period is null)
                {
                    period = new SalaryPeriod { TeacherId = teacherId, Month = key };
                    db.SalaryPeriods.Add(period);
                }
                if (locked) continue;

                var hours = await db.TeacherHours.AsNoTracking()
                    .Where(x => x.TeacherId == teacherId && x.Lesson != null
                        && x.Lesson.StartsAt >= start && x.Lesson.StartsAt < end)
                    .ToListAsync(ct);
                period.TotalScheduledMinutes = hours.Sum(x => x.ScheduledMinutes);
                period.TotalActualMinutes = hours.Sum(x => x.ActualAttendanceMinutes);
                period.TotalProposedMinutes = hours.Sum(x => x.ProposedPayableMinutes);
                period.TotalApprovedMinutes = hours.Sum(LessonPolicyService.ApprovedMinutes);

                var penalties = await db.TeacherPenalties.AsNoTracking()
                    .Where(x => x.TeacherId == teacherId && x.CreatedAt >= start && x.CreatedAt < end).ToListAsync(ct);
                period.ApprovedPenaltyEgp = penalties
                    .Where(x => x.Status is "approved" or "included_in_salary_review").Sum(x => x.AmountEgp);
                period.PendingPenaltyEgp = penalties.Where(x => x.Status == "pending").Sum(x => x.AmountEgp);

                var cancellations = await db.LessonCancellations.AsNoTracking()
                    .Where(x => x.CreatedAt >= start && x.CreatedAt < end && x.Lesson != null && x.Lesson.TeacherId == teacherId)
                    .Select(x => new { x.Outcome })
                    .ToListAsync(ct);
                period.StudentLateCancellations = cancellations.Count(x => x.Outcome is "late_cancellation" or "grace_cancellation");
                period.TeacherCancellations = cancellations.Count(x => x.Outcome is "cancelled_by_teacher" or "late_cancelled_by_teacher");
                period.TeacherNoShows = cancellations.Count(x => x.Outcome == "teacher_no_show");
                period.StudentNoShows = cancellations.Count(x => x.Outcome == "student_no_show");
                period.CompletedLessons = await db.Lessons.CountAsync(x => x.TeacherId == teacherId
                    && x.Status == "completed" && x.StartsAt >= start && x.StartsAt < end, ct);
                period.UpdatedAt = now;
            }

            Audit(db, UserId(p), "salary_periods_generated", "SalaryPeriod", null, $"Generated {key} salary review totals.");
            await db.SaveChangesAsync(ct);
            await realtime.ToUsersAsync(teacherIds, RealtimeScopes.Billing);
            var rows = await db.SalaryPeriods.AsNoTracking().Include(x => x.Teacher)
                .Where(x => x.Month == key).OrderBy(x => x.Teacher!.FullName).ToListAsync(ct);
            return Results.Ok(rows.Select(ToSalary));
        });

        admin.MapPatch("/salary-periods/{id:guid}", async (
            Guid id, UpdateSalaryPeriodRequest input, ClaimsPrincipal p, AcademyDbContext db,
            AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
        {
            var period = await db.SalaryPeriods.Include(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (period is null) return Results.NotFound();
            var status = input.Status?.Trim().ToLowerInvariant();
            if (status is not null && status is not ("open" or "reviewed" or "paid"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Choose open, reviewed, or paid."] });
            if (input.ManualAdjustmentEgp is not null)
            {
                if (Math.Abs(input.ManualAdjustmentEgp.Value) > 1_000_000)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["manualAdjustmentEgp"] = ["Enter a reasonable adjustment."] });
                period.ManualAdjustmentEgp = input.ManualAdjustmentEgp.Value;
            }
            if (input.FinalAmountEgp is not null)
            {
                if (input.FinalAmountEgp.Value < 0 || input.FinalAmountEgp.Value > 10_000_000)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["finalAmountEgp"] = ["Enter a valid final amount."] });
                period.FinalAmountEgp = input.FinalAmountEgp.Value;
            }
            if (input.Notes is not null)
                period.Notes = input.Notes.Trim() is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 1000)] : null;
            if (status is not null)
            {
                period.Status = status;
                if (status == "reviewed")
                {
                    period.ReviewedBy = UserId(p);
                    period.ReviewedAt ??= DateTimeOffset.UtcNow;
                }
                if (status == "paid") period.PaidAt ??= DateTimeOffset.UtcNow;
            }
            period.UpdatedAt = DateTimeOffset.UtcNow;

            var body = status switch
            {
                "reviewed" => $"Your {period.Month} salary review is ready for confirmation.",
                "paid" => $"Your {period.Month} salary was marked paid.",
                _ => $"Your {period.Month} salary record was updated.",
            };
            if (status is "reviewed" or "paid")
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    period.TeacherId, "Salary review", body, "payment", "/earnings", $"salary-{status}:{period.Id}"));
            Audit(db, UserId(p), "salary_period_updated", "SalaryPeriod", period.Id, $"Status {period.Status}.");
            await db.SaveChangesAsync(ct);
            if (status is "reviewed" or "paid")
                await LessonOpsMessaging.PushAsync(db, push, [period.TeacherId], "Salary review", body, "/earnings", ct);
            await realtime.ToUserAsync(period.TeacherId, RealtimeScopes.Billing, period.Id);
            return Results.Ok(ToSalary(period));
        });
    }
}
