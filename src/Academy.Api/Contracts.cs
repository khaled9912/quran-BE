using System.Text.Json.Serialization;

namespace Academy.Api;

public sealed record RegisterRequest(string FullName, string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequestInput(string Email);
public sealed record PasswordResetInput(string Email, string Code, string NewPassword);
public sealed record OtpRequest(string Phone);
public sealed record OtpVerifyRequest(string Phone, string Code, string? FullName);
public sealed record CreateUserRequest(string FullName, string Email, string Password, string Role);
public sealed record CreateStudentRequest(string FullName, string Email, string Password, string? Grade);
public sealed record UpdateUserRequest(string? FullName, string? Email, string? Password, bool? IsActive);
public sealed record UpdateProfileRequest(string? FullName, string? Email, string? PhoneNumber);
public sealed record UpdateLessonRequest(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Status);
public sealed record AssignStudentRequest(Guid TeacherId, Guid StudentId);
public sealed record PublishAvailabilityRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record CreateLessonRequest(Guid TeacherId, Guid StudentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record ContactRequest(string Name, string Email, string Message);
public sealed record RejectRequest(string? Reason);
public sealed record RequestSlotInput(Guid AvailabilitySlotId);
public sealed record SaveLessonReportRequest(string ClassSummary, string StudentFeedback, string Progress);
public sealed record SaveTeacherReviewRequest(int Rating, string? Comment);
public sealed record ReviewTeacherApplicationRequest(string Status, string? Reason);
public sealed record TranscriptSegmentInput(string? Speaker, string? Text, double? AtSeconds);
public sealed record TranscriptUploadRequest(List<TranscriptSegmentInput>? Segments);

public sealed record UserDto(Guid Id, string FullName, string Email, string Role, bool IsActive, [property: JsonPropertyName("full_name")] string FullNameSnake, [property: JsonPropertyName("is_active")] bool IsActiveSnake, string Name, string? PhoneNumber, string? AvatarUrl);
public sealed record AssignmentDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset CreatedAt, bool IsActive, string Status);
public sealed record AvailabilityDto(Guid Id, Guid TeacherId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
public sealed record TeacherAvailabilityDto(UserDto Teacher, IReadOnlyList<AvailabilityDto> Slots, double? AverageRating = null, int ReviewCount = 0, IReadOnlyList<TeacherReviewDto>? Reviews = null);
public sealed record LessonRequestDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, Guid? AvailabilitySlotId, Guid? StudentAvailabilityId, string InitiatedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset CreatedAt, string? RejectionReason);
public sealed record LessonReportDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, string ClassSummary, string StudentFeedback, string Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TeacherReviewDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int Rating, string? Comment, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PublicTeacherDto(Guid Id, string FullName, string? AvatarUrl, double? AverageRating, int ReviewCount, IReadOnlyList<TeacherReviewDto> Reviews);
public sealed record LessonDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset? ActualStartedAt, DateTimeOffset? ActualEndedAt, LessonReportDto? Report, TeacherReviewDto? Review);
public sealed record TeacherApplicationDto(Guid Id, string FullName, string Email, string PhoneNumber, IReadOnlyList<string> Subjects, string EnglishLevel, int ExperienceYears, string Bio, string AvailabilityNote, string Status, string? RejectionReason, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, Guid? TeacherId, string? CvFileName, string? IntroFileName);
