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
    public Guid LessonRequestId { get; set; }
    public LessonRequest? LessonRequest { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "scheduled";
    public DateTimeOffset? ActualStartedAt { get; set; }
    public DateTimeOffset? ActualEndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
