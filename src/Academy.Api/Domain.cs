using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Academy.Api;

public sealed class AcademyUser : IdentityUser<Guid>
{
    [MaxLength(160)] public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TeacherStudentAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AvailabilitySlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    // available | requested | booked
    [MaxLength(20)] public string Status { get; set; } = "available";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A teacher's announced weekly availability. One row is one bookable weekly
/// slot (day + local time window). Booking a rule creates a LessonSeries.
/// </summary>
public sealed class TeacherAvailabilityRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    // 0 = Sunday ... 6 = Saturday
    public int DayOfWeek { get; set; }
    public TimeOnly StartsAtTime { get; set; }
    public TimeOnly EndsAtTime { get; set; }
    [MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A student's permanent weekly booking. Lessons are materialized ahead of time
/// and extended lazily, so a series has no end date.
/// </summary>
public sealed class LessonSeries
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid AvailabilityRuleId { get; set; }
    public TeacherAvailabilityRule? AvailabilityRule { get; set; }
    public int DayOfWeek { get; set; }
    public TimeOnly StartsAtTime { get; set; }
    public TimeOnly EndsAtTime { get; set; }
    [MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A student moving their recurring series to another weekly slot the teacher
/// already published. The move is applied as it is requested, so the row is the
/// record of what happened: applied, or one of the older pending/approved/
/// rejected rows left by the approval queue this replaced.
/// </summary>
public sealed class LessonChangeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeriesId { get; set; }
    public LessonSeries? Series { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid RequestedRuleId { get; set; }
    public TeacherAvailabilityRule? RequestedRule { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "applied";
    [MaxLength(500)] public string? StudentNote { get; set; }
    /// <summary>Only ever written for rows the old approval queue left behind.</summary>
    [MaxLength(500)] public string? AdminNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Only ever written for rows the old approval queue left behind.</summary>
    public DateTimeOffset? AdminRespondedAt { get; set; }
}

public sealed class StudentAvailability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "available";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A time an unassigned student offers to every active teacher. The first
/// teacher to accept books a one-off lesson instantly and closes the offer.
/// </summary>
public sealed class OpenStudentOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    // open | claimed | withdrawn
    [MaxLength(20)] public string Status { get; set; } = "open";
    public Guid? ClaimedByTeacherId { get; set; }
    public AcademyUser? ClaimedByTeacher { get; set; }
    public Guid? LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
}

public sealed class LessonRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid? AvailabilitySlotId { get; set; }
    public AvailabilitySlot? AvailabilitySlot { get; set; }
    public Guid? StudentAvailabilityId { get; set; }
    public StudentAvailability? StudentAvailability { get; set; }
    [MaxLength(24)] public string Status { get; set; } = "pending_teacher";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TeacherRespondedAt { get; set; }
    public DateTimeOffset? AdminRespondedAt { get; set; }
    [MaxLength(500)] public string? RejectionReason { get; set; }
}

public sealed class Lesson
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public Guid? LessonRequestId { get; set; }
    public LessonRequest? LessonRequest { get; set; }
    public Guid? SeriesId { get; set; }
    public LessonSeries? Series { get; set; }
    /// <summary>True for a student's very first lesson, which is a trial.</summary>
    public bool IsTrial { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "scheduled";
    public DateTimeOffset? ActualStartedAt { get; set; }
    public DateTimeOffset? ActualEndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    // Cancellation detail lives beside Status so the existing cancelled value
    // and every client that checks for it keep working.
    [MaxLength(40)] public string? CancellationOutcome { get; set; }
    [MaxLength(1000)] public string? CancelReason { get; set; }
    public Guid? CancelledById { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    /// <summary>A make-up lesson that fulfils an earlier cancelled lesson.</summary>
    public bool IsMakeup { get; set; }
    public Guid? OriginalLessonId { get; set; }
    public LessonReport? Report { get; set; }
    public TeacherReview? Review { get; set; }
}

/// <summary>Teacher's post-class report about the student and the lesson.</summary>
public sealed class LessonReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    [MaxLength(2000)] public string ClassSummary { get; set; } = "";
    [MaxLength(2000)] public string StudentFeedback { get; set; } = "";
    // excellent | good | needs_attention
    [MaxLength(30)] public string Progress { get; set; } = "good";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Student's rating of a teacher after a completed lesson.</summary>
public sealed class TeacherReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    public int Rating { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One transcribed utterance, posted by the Jitsi-side capture bot. Segments are
/// append-only; flags mark attempts to move a lesson off-platform for admin review.
/// </summary>
public sealed class LessonTranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    [MaxLength(120)] public string Speaker { get; set; } = "";
    [MaxLength(4000)] public string Text { get; set; } = "";
    public double? AtSeconds { get; set; }
    // Comma-separated categories: phone, email, platform, offplatform
    [MaxLength(120)] public string Flags { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A support reviewer's assessment of one lesson transcript. Scores are 1-5 and
/// stay null while the review is a draft; submitting requires all four.
/// </summary>
public sealed class LessonQualityReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public Guid ReviewerId { get; set; }
    public AcademyUser? Reviewer { get; set; }
    public int? TeachingClarity { get; set; }
    public int? StudentPractice { get; set; }
    public int? CorrectionQuality { get; set; }
    public int? Engagement { get; set; }
    [MaxLength(2000)] public string? Strengths { get; set; }
    [MaxLength(2000)] public string? Gaps { get; set; }
    [MaxLength(1000)] public string? TeacherAction { get; set; }
    [MaxLength(1000)] public string? StudentAction { get; set; }
    // draft | submitted
    [MaxLength(20)] public string Status { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Audit trail for transcript views: support access must stay accountable, and
/// lesson recordings involve minors.
/// </summary>
public sealed class TranscriptAccessLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AccessedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Admin-set prices for one teacher: what a student pays per lesson in USD and
/// what the teacher is paid per lesson in EGP. A missing row means "not priced".
/// </summary>
public sealed class TeacherBillingRate
{
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    public decimal StudentPriceUsd { get; set; }
    public decimal PayoutEgp { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Where the academy sends a teacher's monthly payout (Egyptian methods).</summary>
public sealed class TeacherPayoutDetail
{
    public Guid TeacherId { get; set; }
    public AcademyUser? Teacher { get; set; }
    [MaxLength(120)] public string InstaPayAccount { get; set; } = "";
    [MaxLength(30)] public string VodafoneCashNumber { get; set; } = "";
    [MaxLength(120)] public string BankName { get; set; } = "";
    [MaxLength(160)] public string BankAccountHolder { get; set; } = "";
    [MaxLength(64)] public string BankIban { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Single academy account (Id = 1) shown to students for USD transfers.</summary>
public sealed class AcademyBankAccount
{
    public int Id { get; set; }
    [MaxLength(120)] public string BankName { get; set; } = "";
    [MaxLength(160)] public string AccountHolder { get; set; } = "";
    [MaxLength(64)] public string AccountNumber { get; set; } = "";
    [MaxLength(64)] public string Iban { get; set; } = "";
    [MaxLength(32)] public string Swift { get; set; } = "";
    [MaxLength(500)] public string Notes { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// A student telling the academy they transferred a month's balance. The amount
/// is frozen when claimed; admins confirm or reject it after checking the bank.
/// </summary>
public sealed class MonthlyPaymentClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public AcademyUser? Student { get; set; }
    /// <summary>Academy-time month key, e.g. 2026-11.</summary>
    [MaxLength(7)] public string Month { get; set; } = "";
    public int LessonCount { get; set; }
    public decimal AmountUsd { get; set; }
    // pending | confirmed | rejected
    [MaxLength(20)] public string Status { get; set; } = "pending";
    [MaxLength(500)] public string? StudentNote { get; set; }
    [MaxLength(500)] public string? AdminNote { get; set; }
    public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
}

/// <summary>
/// An in-app notification for one account. Events are written as they happen
/// (bookings, confirmations) and lesson-time reminders are materialized when the
/// account polls, so the bell is accurate even if the app was closed at the time.
/// </summary>
public sealed class AppNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AcademyUser? User { get; set; }
    [MaxLength(160)] public string Title { get; set; } = "";
    [MaxLength(500)] public string Body { get; set; } = "";
    // booking | lesson | request | report | payment | system
    [MaxLength(30)] public string Kind { get; set; } = "system";
    /// <summary>App-relative destination, e.g. /lesson/{id} or /schedule.</summary>
    [MaxLength(200)] public string? Link { get; set; }
    /// <summary>Stable event key so retries and polling cannot duplicate a notice.</summary>
    [MaxLength(160)] public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class ContactSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(254)] public string Email { get; set; } = "";
    [MaxLength(3000)] public string Message { get; set; } = "";
    [MaxLength(20)] public string Status { get; set; } = "new";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A public application from a prospective teacher, reviewed by an admin.</summary>
public sealed class TeacherApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public string FullName { get; set; } = "";
    [MaxLength(254)] public string Email { get; set; } = "";
    [MaxLength(30)] public string PhoneNumber { get; set; } = "";
    // Comma-separated canonical values: quran,tajweed,islamic_studies,arabic
    [MaxLength(200)] public string Subjects { get; set; } = "";
    // beginner | intermediate | advanced | fluent
    [MaxLength(20)] public string EnglishLevel { get; set; } = "";
    public int ExperienceYears { get; set; }
    [MaxLength(2000)] public string Bio { get; set; } = "";
    [MaxLength(500)] public string AvailabilityNote { get; set; } = "";
    // pending | approved | rejected
    [MaxLength(20)] public string Status { get; set; } = "pending";
    [MaxLength(500)] public string? RejectionReason { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? TeacherId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public TeacherApplicationCv? Cv { get; set; }
    public TeacherApplicationIntro? Intro { get; set; }
    public TeacherApplicationQuranRecording? QuranRecording { get; set; }
}

/// <summary>CV stored separately so application lists never load file bytes.</summary>
public sealed class TeacherApplicationCv
{
    public Guid ApplicationId { get; set; }
    public TeacherApplication? Application { get; set; }
    [MaxLength(200)] public string FileName { get; set; } = "";
    [MaxLength(100)] public string ContentType { get; set; } = "application/pdf";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Self-introduction recording, also kept out of application list queries.</summary>
public sealed class TeacherApplicationIntro
{
    public Guid ApplicationId { get; set; }
    public TeacherApplication? Application { get; set; }
    [MaxLength(200)] public string FileName { get; set; } = "";
    [MaxLength(100)] public string ContentType { get; set; } = "audio/webm";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Quran recitation sample, kept out of application list queries.</summary>
public sealed class TeacherApplicationQuranRecording
{
    public Guid ApplicationId { get; set; }
    public TeacherApplication? Application { get; set; }
    [MaxLength(200)] public string FileName { get; set; } = "";
    [MaxLength(100)] public string ContentType { get; set; } = "audio/webm";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Profile image stored separately so user queries never load image bytes.</summary>
public sealed class UserAvatar
{
    public Guid UserId { get; set; }
    public AcademyUser? User { get; set; }
    [MaxLength(100)] public string ContentType { get; set; } = "image/png";
    public byte[] Data { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
