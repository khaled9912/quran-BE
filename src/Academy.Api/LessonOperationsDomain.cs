using System.ComponentModel.DataAnnotations;

namespace Academy.Api;

/// <summary>Single row of configurable academy lesson-operations policy.</summary>
public sealed class AcademyPolicy
{
    public int Id { get; set; } = 1;
    public int StudentFreeCancelHours { get; set; } = 24;
    public int TeacherFreeCancelHours { get; set; } = 24;
    public int NoShowGraceMinutes { get; set; } = 10;
    public int RescheduleResponseHours { get; set; } = 12;
    public int StudentGraceCancellationsPerMonth { get; set; } = 1;
    public int StudentWarningStrikeThreshold { get; set; } = 3;
    public int StudentAdminReviewStrikeThreshold { get; set; } = 4;
    public int TeacherAdminReviewStrikeThreshold { get; set; } = 2;
    public int TrialNoShowThreshold { get; set; } = 2;
    /// <summary>Per-lesson payouts treat a lesson with both participants present as fully payable.</summary>
    public bool PayScheduledDurationWhenAttended { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Immutable record of why and how a lesson was cancelled.</summary>
public sealed class LessonCancellation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid? CancelledById { get; set; }
    // student | teacher | admin | system
    [MaxLength(20)] public string ActorRole { get; set; } = "system";
    // free_cancellation | late_cancellation | grace_cancellation | cancelled_by_teacher |
    // late_cancelled_by_teacher | student_no_show | teacher_no_show | both_absent | admin_cancelled
    [MaxLength(40)] public string Outcome { get; set; } = "";
    public bool IsLate { get; set; }
    public bool UsedGrace { get; set; }
    public double HoursBefore { get; set; }
    public int TeacherPayableMinutes { get; set; }
    [MaxLength(1000)] public string? Reason { get; set; }
    /// <summary>Policy values that were in force when the decision was made.</summary>
    [MaxLength(2000)] public string PolicySnapshot { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One participant's proposal to move a lesson. The original lesson is untouched
/// until the other party accepts, and at most one request is pending per lesson.
/// </summary>
public sealed class LessonRescheduleRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid RequestedById { get; set; }
    [MaxLength(20)] public string RequestedByRole { get; set; } = "student";
    public DateTimeOffset ProposedStartsAt { get; set; }
    public DateTimeOffset ProposedEndsAt { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
    // pending | accepted | declined | expired | cancelled
    [MaxLength(20)] public string Status { get; set; } = "pending";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? RespondedById { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    [MaxLength(500)] public string? ResponseNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Preserves every previous lesson time so reschedules remain auditable.</summary>
public sealed class LessonScheduleChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public DateTimeOffset FromStartsAt { get; set; }
    public DateTimeOffset FromEndsAt { get; set; }
    public DateTimeOffset ToStartsAt { get; set; }
    public DateTimeOffset ToEndsAt { get; set; }
    public Guid? ChangedById { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-participant summary derived from idempotent join/leave events.</summary>
public sealed class LessonAttendance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid UserId { get; set; }
    public AcademyUser? User { get; set; }
    // teacher | student
    [MaxLength(20)] public string Role { get; set; } = "student";
    // not_joined | joined | left | absent
    [MaxLength(20)] public string Status { get; set; } = "not_joined";
    public DateTimeOffset? FirstJoinedAt { get; set; }
    public DateTimeOffset? LastLeftAt { get; set; }
    public int TotalSeconds { get; set; }
    public int ReconnectionCount { get; set; }
    public bool JoinedLate { get; set; }
    public bool LeftEarly { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A client-supplied attendance event. EventId makes retries safe: a repeated
/// join/leave beacon can never create a second interval.
/// </summary>
public sealed class LessonAttendanceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(120)] public string DeviceId { get; set; } = "";
    [MaxLength(80)] public string EventId { get; set; } = "";
    // join | leave
    [MaxLength(10)] public string Action { get; set; } = "join";
    public DateTimeOffset ClientAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One continuous presence window for a participant on one device.</summary>
public sealed class LessonAttendanceInterval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Guid AttendanceId { get; set; }
    public LessonAttendance? Attendance { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(120)] public string DeviceId { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Append-only ledger of student lesson entitlement changes.</summary>
public sealed class LessonCreditTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid? LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    // lesson_reserved | lesson_consumed | lesson_restored | late_cancellation |
    // grace_cancellation | excuse_approved | teacher_cancellation | teacher_no_show | admin_adjustment
    [MaxLength(40)] public string Type { get; set; } = "";
    public int Amount { get; set; }
    [MaxLength(500)] public string Reason { get; set; } = "";
    public Guid? CreatedBy { get; set; }
    [MaxLength(40)] public string CreatedByRole { get; set; } = "system";
    public Guid? RelatedTransactionId { get; set; }
    /// <summary>Idempotency key, e.g. restore:{lessonId}.</summary>
    [MaxLength(200)] public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Three independent values for every lesson: actual presence, proposed payable
/// time, and the admin-approved amount. None is ever overwritten by another.
/// </summary>
public sealed class TeacherHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid TeacherId { get; set; }
    public int ScheduledMinutes { get; set; }
    public int ActualAttendanceMinutes { get; set; }
    public int ProposedPayableMinutes { get; set; }
    public int? ApprovedPayableMinutes { get; set; }
    // pending | approved | adjusted | rejected | not_applicable
    [MaxLength(20)] public string ApprovalStatus { get; set; } = "pending";
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    [MaxLength(500)] public string? AdjustmentReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Financial penalty that only ever affects a manually reviewed salary.</summary>
public sealed class TeacherPenalty
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid? LessonId { get; set; }
    [MaxLength(60)] public string EventType { get; set; } = "";
    public decimal AmountEgp { get; set; }
    [MaxLength(500)] public string Reason { get; set; } = "";
    // pending | approved | rejected | included_in_salary_review
    [MaxLength(30)] public string Status { get; set; } = "pending";
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    [MaxLength(500)] public string? AdminNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Operational reliability history for students and teachers.</summary>
public sealed class ReliabilityEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AcademyUser? User { get; set; }
    // student | teacher
    [MaxLength(20)] public string Role { get; set; } = "student";
    public Guid? LessonId { get; set; }
    // student_late_cancellation | student_grace_cancellation | student_no_show |
    // student_warning | student_admin_review_required |
    // teacher_early_cancellation | teacher_late_cancellation | teacher_no_show |
    // teacher_admin_review_required | admin_warning
    [MaxLength(50)] public string Type { get; set; } = "";
    [MaxLength(500)] public string? Notes { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    [MaxLength(500)] public string? ResolutionNote { get; set; }
    [MaxLength(50)] public string? ResolutionAction { get; set; }
}

/// <summary>Monthly salary review prepared by the system, completed by an admin.</summary>
public sealed class SalaryPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    /// <summary>Academy-time month key, e.g. 2026-11.</summary>
    [MaxLength(7)] public string Month { get; set; } = "";
    // open | reviewed | paid
    [MaxLength(20)] public string Status { get; set; } = "open";
    public int TotalScheduledMinutes { get; set; }
    public int TotalActualMinutes { get; set; }
    public int TotalProposedMinutes { get; set; }
    public int TotalApprovedMinutes { get; set; }
    public decimal ApprovedPenaltyEgp { get; set; }
    public decimal PendingPenaltyEgp { get; set; }
    public int CompletedLessons { get; set; }
    public int StudentLateCancellations { get; set; }
    public int TeacherCancellations { get; set; }
    public int TeacherNoShows { get; set; }
    public int StudentNoShows { get; set; }
    public decimal ManualAdjustmentEgp { get; set; }
    public decimal? FinalAmountEgp { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Administrative audit trail for sensitive manual changes.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; }
    [MaxLength(80)] public string Action { get; set; } = "";
    [MaxLength(60)] public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    [MaxLength(2000)] public string Detail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A student's request to excuse a late cancellation or no-show.</summary>
public sealed class LessonExcuseRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    // illness | family_emergency | technical | other
    [MaxLength(40)] public string Category { get; set; } = "other";
    [MaxLength(2000)] public string Explanation { get; set; } = "";
    // pending | approved | rejected
    [MaxLength(20)] public string Status { get; set; } = "pending";
    [MaxLength(1000)] public string? AdminNote { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Attendance issue raised automatically or by a participant.</summary>
public sealed class LessonIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    // potential_teacher_no_show | potential_student_no_show | both_absent | attendance_dispute
    [MaxLength(40)] public string Type { get; set; } = "";
    // open | resolved | dismissed
    [MaxLength(20)] public string Status { get; set; } = "open";
    [MaxLength(1000)] public string Detail { get; set; } = "";
    public Guid? ReportedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    [MaxLength(1000)] public string? ResolutionNote { get; set; }
}

/// <summary>Expo push token for one installation. Multiple per user are allowed.</summary>
public sealed class DeviceToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AcademyUser? User { get; set; }
    [MaxLength(200)] public string Token { get; set; } = "";
    [MaxLength(20)] public string Platform { get; set; } = "android";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
