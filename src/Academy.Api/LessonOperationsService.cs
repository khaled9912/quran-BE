using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// One place decides what a cancellation means. Both the web and mobile clients
/// call the preview endpoint, show this result, and the cancel endpoint
/// re-evaluates it server-side before changing anything.
/// </summary>
public static class LessonPolicyService
{
    private static readonly TimeSpan CairoOffsetFallback = TimeSpan.FromHours(2);

    public static async Task<AcademyPolicy> GetPolicyAsync(AcademyDbContext db, CancellationToken ct = default)
    {
        var policy = await db.AcademyPolicies.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (policy is not null) return policy;
        policy = new AcademyPolicy();
        db.AcademyPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public static async Task ExpireRescheduleRequestsAsync(
        AcademyDbContext db, DateTimeOffset now, AcademyPushSender push, RealtimeNotifier realtime,
        Guid? lessonId = null, CancellationToken ct = default)
    {
        var expiring = await db.LessonRescheduleRequests.AsNoTracking()
            .Include(x => x.Lesson).ThenInclude(x => x!.Teacher)
            .Include(x => x.Lesson).ThenInclude(x => x!.Student)
            .Include(x => x.Lesson).ThenInclude(x => x!.Series)
            .Where(x => x.Status == "pending" && x.ExpiresAt <= now
                && (lessonId == null || x.LessonId == lessonId))
            .ToListAsync(ct);

        foreach (var request in expiring)
        {
            var lesson = request.Lesson!;
            AppNotification notice;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                // The conditional update and notice commit together, even when two callers race.
                var changed = await db.LessonRescheduleRequests
                    .Where(x => x.Id == request.Id && x.Status == "pending" && x.ExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "expired")
                        .SetProperty(x => x.RespondedAt, now), ct);
                if (changed == 0) continue;

                var isMakeup = lesson.Status == "cancelled";
                var waitedFor = isMakeup || request.RequestedById == lesson.TeacherId
                    ? lesson.Student?.FullName ?? "The student"
                    : lesson.Teacher?.FullName ?? "The teacher";
                var when = LessonOpsMessaging.When(request.ProposedStartsAt, lesson.Series?.TimeZoneId);
                var title = isMakeup ? "Make-up proposal expired" : "Reschedule request expired";
                var body = isMakeup
                    ? $"{waitedFor} did not reply to your make-up proposal for {when}. No make-up was booked; you can propose another time."
                    : $"{waitedFor} did not reply to your proposal for {when}. This request did not change the lesson time. Open the lesson for its current schedule.";
                notice = LessonOpsMessaging.Notice(request.RequestedById, title, body,
                    "lesson", $"/lesson/{lesson.Id}", $"reschedule-expired:{request.Id}");
                db.Notifications.Add(notice);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            await LessonOpsMessaging.PushAsync(db, push, [notice.UserId], notice.Title, notice.Body, notice.Link, ct);
            await realtime.ToUserAsync(notice.UserId, RealtimeScopes.Notifications);
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
        }
    }

    public static AcademyPolicyDto ToDto(AcademyPolicy p) => new(
        p.StudentFreeCancelHours,
        p.TeacherFreeCancelHours,
        p.NoShowGraceMinutes,
        p.RescheduleResponseHours,
        p.StudentGraceCancellationsPerMonth,
        p.StudentWarningStrikeThreshold,
        p.StudentAdminReviewStrikeThreshold,
        p.TeacherAdminReviewStrikeThreshold,
        p.TrialNoShowThreshold,
        p.PayScheduledDurationWhenAttended,
        p.UpdatedAt);

    public static int ScheduledMinutes(Lesson lesson) =>
        Math.Max(0, (int)Math.Round((lesson.EndsAt - lesson.StartsAt).TotalMinutes));

    public static string MonthKey(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, CairoTimeZone());
        return local.ToString("yyyy-MM");
    }

    /// <summary>UTC bounds of one academy-time month (Africa/Cairo).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, CairoTimeZone());
        return MonthBounds(local.Year, local.Month);
    }

    public static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(int year, int month)
    {
        var tz = CairoTimeZone();
        var startLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddMonths(1);
        return (ToUtc(startLocal, tz), ToUtc(endLocal, tz));
    }

    /// <summary>Bounds for a "yyyy-MM" key, falling back to the current academy month.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, string Key) MonthBounds(string? key, DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, CairoTimeZone());
        var year = localNow.Year;
        var month = localNow.Month;
        if (!string.IsNullOrWhiteSpace(key) &&
            DateTime.TryParseExact(key + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            year = parsed.Year;
            month = parsed.Month;
        }
        var (start, end) = MonthBounds(year, month);
        return (start, end, $"{year:0000}-{month:00}");
    }

    /// <summary>Academy-time calendar day bounds for the admin monitoring view.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, string Key) DayBounds(string? date, DateTimeOffset now)
    {
        var tz = CairoTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var day = localNow.Date;
        if (!string.IsNullOrWhiteSpace(date) &&
            DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            day = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        var start = ToUtc(day, tz);
        var end = ToUtc(day.AddDays(1), tz);
        return (start, end, day.ToString("yyyy-MM-dd"));
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
        => new(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);

    private static TimeZoneInfo CairoTimeZone()
    {
        foreach (var id in new[] { "Africa/Cairo", "Egypt Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Academy-Cairo", CairoOffsetFallback, "Academy Cairo", "Academy Cairo");
    }

    public static async Task<int> GraceUsedThisMonthAsync(
        AcademyDbContext db,
        Guid studentId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var (start, end) = MonthBounds(now);
        return await db.LessonCancellations
            .Where(x => x.UsedGrace
                && x.CreatedAt >= start
                && x.CreatedAt < end
                && x.Lesson != null
                && x.Lesson.StudentId == studentId)
            .CountAsync(ct);
    }

    public static async Task<CancellationPreviewDto> EvaluateCancellationAsync(
        AcademyDbContext db,
        Lesson lesson,
        string actorRole,
        DateTimeOffset now,
        AcademyPolicy policy,
        CancellationToken ct = default)
    {
        var role = actorRole is "super_admin" ? "admin" : actorRole;
        var scheduled = ScheduledMinutes(lesson);
        var hoursBefore = Math.Round((lesson.StartsAt - now).TotalHours, 2);
        var graceUsed = role == "student"
            ? await GraceUsedThisMonthAsync(db, lesson.StudentId, now, ct)
            : 0;
        var graceLimit = policy.StudentGraceCancellationsPerMonth;
        var policyDto = ToDto(policy);

        CancellationPreviewDto Blocked(string reason) => new(
            false, reason, role, "blocked", false, false, graceUsed, graceLimit,
            hoursBefore, scheduled, 0, 0, "none", null, false, reason, lesson.CancellationOutcome, policyDto);

        if (lesson.Status == "cancelled")
            return Blocked("This lesson is already cancelled.");
        if (lesson.Status == "completed")
            return Blocked("This lesson is already completed and cannot be cancelled.");
        if (lesson.EndsAt <= now)
            return Blocked("This lesson has already ended. Contact the academy if something went wrong.");
        if (lesson.StartsAt <= now && role != "admin")
            return Blocked("This lesson has already started. Contact the academy if you cannot attend.");

        CancellationPreviewDto Result(
            string outcome,
            bool isLate,
            bool usedGrace,
            int payable,
            int creditChange,
            string creditEffect,
            string? reliability,
            bool adminReview,
            string explanation) => new(
                true, null, role, outcome, isLate, usedGrace, graceUsed, graceLimit,
                hoursBefore, scheduled, payable, creditChange, creditEffect, reliability,
                adminReview, explanation, null, policyDto);

        if (role == "student")
        {
            var graceAvailable = graceUsed < graceLimit;
            if (hoursBefore >= policy.StudentFreeCancelHours)
                return Result(
                    "free_cancellation", false, false, 0, 0, "none", null, false,
                    $"This is more than {policy.StudentFreeCancelHours} hours before the lesson. Nothing is counted against you and no reliability strike is recorded. Your teacher is not paid for this slot.");

            if (graceAvailable)
                return Result(
                    "grace_cancellation", true, true, scheduled,
                    0, "none", "student_grace_cancellation", false,
                    $"This is inside the {policy.StudentFreeCancelHours}-hour window. This uses your {graceLimit} grace cancellation for this month: nothing is counted against you and no strike is recorded. Your teacher is paid for the booked time.");

            return Result(
                "late_cancellation", true, false, scheduled,
                lesson.IsTrial ? 0 : -1, lesson.IsTrial ? "none" : "deduct",
                "student_late_cancellation", false,
                lesson.IsTrial
                    ? $"This is inside the {policy.StudentFreeCancelHours}-hour window. A trial lesson leaves nothing counted against you, but this late cancellation is recorded as a reliability strike."
                    : $"This is inside the {policy.StudentFreeCancelHours}-hour window. The late cancellation is recorded on your account, a reliability strike is recorded, and your teacher is paid for the booked time.");
        }

        if (role == "teacher")
        {
            if (hoursBefore >= policy.TeacherFreeCancelHours)
                return Result(
                    "cancelled_by_teacher", false, false, 0,
                    lesson.IsTrial ? 0 : 1, lesson.IsTrial ? "none" : "restore",
                    "teacher_early_cancellation", false,
                    $"This is more than {policy.TeacherFreeCancelHours} hours before the lesson. The student is notified, the class is not counted against them, and no teacher compensation is generated.");

            return Result(
                "late_cancelled_by_teacher", true, false, 0,
                lesson.IsTrial ? 0 : 1, lesson.IsTrial ? "none" : "restore",
                "teacher_late_cancellation", true,
                $"This is inside the {policy.TeacherFreeCancelHours}-hour window. The student is notified and the class is not counted against them. This late cancellation is recorded for academy review and may affect your reliability record.");
        }

        if (role == "admin")
            return Result(
                "admin_cancelled", hoursBefore < policy.TeacherFreeCancelHours, false, scheduled,
                lesson.IsTrial ? 0 : 1, lesson.IsTrial ? "none" : "restore",
                null, false,
                "Academy cancellation: the class is not counted against the student and the teacher is paid for the booked time.");

        return Blocked("You do not have permission to cancel this lesson.");
    }

    public static void ApplyCancellation(
        AcademyDbContext db,
        Lesson lesson,
        CancellationPreviewDto decision,
        Guid actorId,
        string reason,
        DateTimeOffset now)
    {
        lesson.Status = "cancelled";
        lesson.CancellationOutcome = decision.Outcome;
        lesson.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        lesson.CancelledById = actorId;
        lesson.CancelledAt = now;

        db.LessonCancellations.Add(new LessonCancellation
        {
            LessonId = lesson.Id,
            CancelledById = actorId,
            ActorRole = decision.ActorRole,
            Outcome = decision.Outcome,
            IsLate = decision.IsLate,
            UsedGrace = decision.UsedGrace,
            HoursBefore = decision.HoursBefore,
            TeacherPayableMinutes = decision.TeacherPayableMinutes,
            Reason = lesson.CancelReason,
            PolicySnapshot = JsonSerializer.Serialize(decision.Policy),
            CreatedAt = now,
        });

        if (decision.CreditChange != 0)
            AddCredit(
                db,
                lesson.StudentId,
                lesson.Id,
                decision.Outcome,
                decision.CreditChange,
                CreditReasonFor(decision.Outcome),
                actorId,
                decision.ActorRole,
                $"cancel:{lesson.Id}");
    }

    public static void AddCredit(
        AcademyDbContext db,
        Guid studentId,
        Guid? lessonId,
        string type,
        int amount,
        string reason,
        Guid? createdBy,
        string createdByRole,
        string dedupeKey)
    {
        if (db.LessonCreditTransactions.Local.Any(x => x.DedupeKey == dedupeKey)) return;
        db.LessonCreditTransactions.Add(new LessonCreditTransaction
        {
            StudentId = studentId,
            LessonId = lessonId,
            Type = type,
            Amount = amount,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            CreatedBy = createdBy,
            CreatedByRole = createdByRole,
            DedupeKey = dedupeKey,
        });
    }

    private static string CreditReasonFor(string outcome) => outcome switch
    {
        "late_cancellation" => "Late student cancellation",
        "student_no_show" => "Student no-show",
        "cancelled_by_teacher" or "late_cancelled_by_teacher" => "Lesson cancelled by teacher",
        "teacher_no_show" => "Teacher no-show",
        "admin_cancelled" => "Lesson cancelled by the academy",
        _ => outcome,
    };

    public sealed record ReliabilityOutcome(bool WarningIssued, bool AdminReviewRequired);

    /// <summary>
    /// Records the strike only once per lesson and raises the monthly warning /
    /// admin review markers at the configured thresholds.
    /// </summary>
    public static async Task<ReliabilityOutcome> AddReliabilityEventAsync(
        AcademyDbContext db,
        Guid userId,
        string role,
        Guid lessonId,
        string type,
        string? notes,
        Guid? createdBy,
        AcademyPolicy policy,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (rolesUnavailable(role)) return new(false, false);

        var already = await db.ReliabilityEvents.AnyAsync(
            x => x.UserId == userId && x.LessonId == lessonId && x.Type == type, ct);
        if (already) return new(false, false);

        db.ReliabilityEvents.Add(new ReliabilityEvent
        {
            UserId = userId,
            Role = role,
            LessonId = lessonId,
            Type = type,
            Notes = notes is { Length: > 500 } ? notes[..500] : notes,
            CreatedBy = createdBy,
            CreatedAt = now,
        });

        var (start, end) = MonthBounds(now);
        if (role == "student")
        {
            var strikeTypes = new[] { "student_late_cancellation", "student_no_show" };
            var strikes = await db.ReliabilityEvents.CountAsync(
                x => x.UserId == userId && strikeTypes.Contains(x.Type)
                    && x.CreatedAt >= start && x.CreatedAt < end, ct);
            if (strikeTypes.Contains(type)) strikes++;

            var warned = await db.ReliabilityEvents.AnyAsync(
                x => x.UserId == userId && x.Type == "student_warning"
                    && x.CreatedAt >= start && x.CreatedAt < end, ct);
            var review = await db.ReliabilityEvents.AnyAsync(
                x => x.UserId == userId && x.Type == "student_admin_review_required"
                    && x.CreatedAt >= start && x.CreatedAt < end, ct);

            var warningIssued = strikes >= policy.StudentWarningStrikeThreshold && !warned;
            var reviewNeeded = strikes >= policy.StudentAdminReviewStrikeThreshold && !review;
            if (warningIssued)
                db.ReliabilityEvents.Add(new ReliabilityEvent
                {
                    UserId = userId,
                    Role = "student",
                    LessonId = lessonId,
                    Type = "student_warning",
                    Notes = $"{strikes} cancellations or no-shows this month. Please contact the academy before booking again.",
                    CreatedBy = createdBy,
                    CreatedAt = now,
                });
            if (reviewNeeded)
                db.ReliabilityEvents.Add(new ReliabilityEvent
                {
                    UserId = userId,
                    Role = "student",
                    LessonId = lessonId,
                    Type = "student_admin_review_required",
                    Notes = $"{strikes} cancellations or no-shows this month.",
                    CreatedBy = createdBy,
                    CreatedAt = now,
                });
            return new(warningIssued, reviewNeeded);
        }

        if (role == "teacher")
        {
            var strikeTypes = new[] { "teacher_late_cancellation", "teacher_no_show" };
            var strikes = await db.ReliabilityEvents.CountAsync(
                x => x.UserId == userId && strikeTypes.Contains(x.Type)
                    && x.CreatedAt >= start && x.CreatedAt < end, ct);
            if (strikeTypes.Contains(type)) strikes++;

            var review = await db.ReliabilityEvents.AnyAsync(
                x => x.UserId == userId && x.Type == "teacher_admin_review_required"
                    && x.CreatedAt >= start && x.CreatedAt < end, ct);
            if (strikes >= policy.TeacherAdminReviewStrikeThreshold && !review)
            {
                db.ReliabilityEvents.Add(new ReliabilityEvent
                {
                    UserId = userId,
                    Role = "teacher",
                    LessonId = lessonId,
                    Type = "teacher_admin_review_required",
                    Notes = $"{strikes} late cancellations or no-shows this month.",
                    CreatedBy = createdBy,
                    CreatedAt = now,
                });
                return new(false, true);
            }
        }

        return new(false, false);

        static bool rolesUnavailable(string r) => r is not ("student" or "teacher");
    }

    public static async Task<int> CountTrialStudentNoShowsAsync(
        AcademyDbContext db,
        Guid studentId,
        CancellationToken ct = default)
    {
        var trialIds = db.Lessons.Where(x => x.IsTrial).Select(x => x.Id);
        return await db.ReliabilityEvents
            .Where(x => x.UserId == studentId && x.Type == "student_no_show"
                && x.LessonId != null && trialIds.Contains(x.LessonId.Value))
            .CountAsync(ct);
    }

    public static int OverlapSeconds(
        DateTimeOffset joinedAt,
        DateTimeOffset? leftAt,
        DateTimeOffset lessonStart,
        DateTimeOffset lessonEnd,
        DateTimeOffset now)
    {
        var from = joinedAt > lessonStart ? joinedAt : lessonStart;
        var to = leftAt ?? (now < lessonEnd ? now : lessonEnd);
        if (to > lessonEnd) to = lessonEnd;
        return to > from ? (int)Math.Round((to - from).TotalSeconds) : 0;
    }

    public static void RecomputeAttendance(
        LessonAttendance attendance,
        Lesson lesson,
        IReadOnlyList<LessonAttendanceInterval> intervals,
        DateTimeOffset now,
        AcademyPolicy policy)
    {
        var total = intervals.Sum(i => OverlapSeconds(i.JoinedAt, i.LeftAt, lesson.StartsAt, lesson.EndsAt, now));
        var open = intervals.Any(i => i.LeftAt is null);
        var lastLeft = intervals.Where(i => i.LeftAt is not null).Select(i => i.LeftAt).Max();
        var firstJoined = intervals.Count == 0 ? (DateTimeOffset?)null : intervals.Min(i => i.JoinedAt);
        var ended = lesson.Status is "cancelled" or "completed" || lesson.EndsAt <= now;

        attendance.TotalSeconds = total;
        attendance.FirstJoinedAt = firstJoined;
        attendance.LastLeftAt = lastLeft;
        attendance.Status = open ? "joined" : total > 0 ? "left" : ended ? "absent" : "not_joined";
        attendance.ReconnectionCount = Math.Max(0, intervals.Count - 1);
        attendance.JoinedLate = firstJoined is not null
            && firstJoined > lesson.StartsAt.AddMinutes(policy.NoShowGraceMinutes);
        attendance.LeftEarly = lastLeft is not null && lastLeft < lesson.EndsAt;
        attendance.UpdatedAt = now;
    }

    public static async Task CloseAttendanceAsync(
        AcademyDbContext db,
        Lesson lesson,
        DateTimeOffset now,
        AcademyPolicy policy,
        CancellationToken ct = default)
    {
        var attendances = await db.LessonAttendances.Where(x => x.LessonId == lesson.Id).ToListAsync(ct);
        if (attendances.Count == 0) return;
        var intervals = await db.LessonAttendanceIntervals.Where(x => x.LessonId == lesson.Id).ToListAsync(ct);
        var closeAt = now < lesson.EndsAt ? now : lesson.EndsAt;
        foreach (var interval in intervals.Where(x => x.LeftAt is null))
            interval.LeftAt = closeAt;
        foreach (var attendance in attendances)
        {
            var mine = intervals.Where(x => x.UserId == attendance.UserId).ToList();
            RecomputeAttendance(attendance, lesson, mine, closeAt, policy);
        }
    }

    public static async Task<AttendanceSummaryDto> BuildAttendanceSummaryAsync(
        AcademyDbContext db,
        Lesson lesson,
        DateTimeOffset now,
        AcademyPolicy policy,
        CancellationToken ct = default)
    {
        var attendances = await db.LessonAttendances.AsNoTracking()
            .Where(x => x.LessonId == lesson.Id)
            .ToListAsync(ct);
        var intervals = await db.LessonAttendanceIntervals.AsNoTracking()
            .Where(x => x.LessonId == lesson.Id)
            .ToListAsync(ct);

        AttendanceParticipantDto Participant(Guid userId, string name, string role)
        {
            var row = attendances.FirstOrDefault(x => x.UserId == userId);
            var mine = intervals.Where(x => x.UserId == userId).ToList();
            var total = mine.Sum(i => OverlapSeconds(i.JoinedAt, i.LeftAt, lesson.StartsAt, lesson.EndsAt, now));
            var open = mine.Any(i => i.LeftAt is null);
            var ended = lesson.Status is "cancelled" or "completed" || lesson.EndsAt <= now;
            var status = open ? "joined" : total > 0 ? "left" : ended ? "absent" : "not_joined";
            var first = mine.Count == 0 ? (DateTimeOffset?)null : mine.Min(x => x.JoinedAt);
            var last = mine.Where(x => x.LeftAt is not null).Select(x => x.LeftAt).Max();
            return new AttendanceParticipantDto(
                userId,
                name,
                role,
                status,
                row?.FirstJoinedAt ?? first,
                row?.LastLeftAt ?? last,
                total,
                Math.Max(row?.ReconnectionCount ?? 0, Math.Max(0, mine.Count - 1)),
                row?.JoinedLate ?? (first is not null && first > lesson.StartsAt.AddMinutes(policy.NoShowGraceMinutes)),
                row?.LeftEarly ?? (last is not null && last < lesson.EndsAt));
        }

        return new AttendanceSummaryDto(
            lesson.Id,
            lesson.Status,
            lesson.StartsAt,
            lesson.EndsAt,
            now,
            [
                Participant(lesson.TeacherId, ApiEndpoints.DisplayName(lesson.Teacher?.FullName) ?? "Teacher", "teacher"),
                Participant(lesson.StudentId, ApiEndpoints.DisplayName(lesson.Student?.FullName) ?? "Student", "student"),
            ]);
    }

    public static async Task<TeacherHours> EnsureTeacherHoursAsync(
        AcademyDbContext db,
        Lesson lesson,
        AcademyPolicy policy,
        DateTimeOffset now,
        int? payableMinutes = null,
        CancellationToken ct = default)
    {
        var hours = await db.TeacherHours.FirstOrDefaultAsync(x => x.LessonId == lesson.Id, ct);
        if (hours is null)
        {
            hours = new TeacherHours { LessonId = lesson.Id, TeacherId = lesson.TeacherId };
            db.TeacherHours.Add(hours);
        }

        var teacherAttendance = db.LessonAttendances.Local
            .FirstOrDefault(x => x.LessonId == lesson.Id && x.UserId == lesson.TeacherId)
            ?? await db.LessonAttendances.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LessonId == lesson.Id && x.UserId == lesson.TeacherId, ct);
        var studentAttendance = db.LessonAttendances.Local
            .FirstOrDefault(x => x.LessonId == lesson.Id && x.UserId == lesson.StudentId)
            ?? await db.LessonAttendances.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LessonId == lesson.Id && x.UserId == lesson.StudentId, ct);
        var scheduled = ScheduledMinutes(lesson);
        var actual = teacherAttendance is null ? 0 : Math.Clamp(teacherAttendance.TotalSeconds / 60, 0, scheduled);

        hours.TeacherId = lesson.TeacherId;
        hours.ScheduledMinutes = scheduled;
        hours.ActualAttendanceMinutes = actual;
        if (hours.ApprovalStatus is not ("approved" or "adjusted"))
        {
            hours.ProposedPayableMinutes = payableMinutes ?? (
                lesson.CancellationOutcome is not null ? 0
                : actual == 0 ? 0
                : policy.PayScheduledDurationWhenAttended && (studentAttendance?.TotalSeconds ?? 0) > 0
                    ? scheduled
                    : actual);
            if (hours.ApprovalStatus is "not_applicable")
                hours.ApprovalStatus = "pending";
        }
        hours.UpdatedAt = now;
        return hours;
    }

    public static int ApprovedMinutes(TeacherHours hours) =>
        hours.ApprovedPayableMinutes ?? hours.ProposedPayableMinutes;
}
