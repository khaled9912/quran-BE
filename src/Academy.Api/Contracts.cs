using System.Text.Json.Serialization;

namespace Academy.Api;

public sealed record RegisterRequest(string FullName, string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record OtpRequest(string Phone);
public sealed record OtpVerifyRequest(string Phone, string Code, string? FullName);
public sealed record CreateUserRequest(string FullName, string Email, string Password, string Role);
public sealed record CreateStudentRequest(string FullName, string Email, string Password, string? Grade);
public sealed record UpdateUserRequest(string? FullName, string? Email, string? Password, bool? IsActive);
public sealed record UpdateLessonRequest(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Status);
public sealed record AssignStudentRequest(Guid TeacherId, Guid StudentId);
public sealed record PublishAvailabilityRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record CreateLessonRequest(Guid TeacherId, Guid StudentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record ContactRequest(string Name, string Email, string Message);
public sealed record RejectRequest(string? Reason);
public sealed record RequestSlotInput(Guid AvailabilitySlotId);

public sealed record UserDto(Guid Id, string FullName, string Email, string Role, bool IsActive, [property: JsonPropertyName("full_name")] string FullNameSnake, [property: JsonPropertyName("is_active")] bool IsActiveSnake, string Name);
public sealed record AssignmentDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset CreatedAt, bool IsActive, string Status);
public sealed record AvailabilityDto(Guid Id, Guid TeacherId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
public sealed record TeacherAvailabilityDto(UserDto Teacher, IReadOnlyList<AvailabilityDto> Slots);
public sealed record LessonRequestDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, Guid? AvailabilitySlotId, Guid? StudentAvailabilityId, string InitiatedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset CreatedAt, string? RejectionReason);
public sealed record LessonDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset? ActualStartedAt, DateTimeOffset? ActualEndedAt);
