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
