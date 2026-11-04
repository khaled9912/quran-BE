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
public sealed record CreateAvailabilityRulesRequest(List<int>? DaysOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId);
public sealed record BookWeeklySlotsRequest(List<Guid>? RuleIds);
public sealed record CreateLessonChangeRequestInput(Guid RequestedRuleId, string? Note);
public sealed record RejectChangeRequestInput(string? Note);
public sealed record SaveLessonReportRequest(string ClassSummary, string StudentFeedback, string Progress);
public sealed record SaveTeacherReviewRequest(int Rating, string? Comment);
public sealed record ReviewTeacherApplicationRequest(string Status, string? Reason);
public sealed record TranscriptSegmentInput(string? Speaker, string? Text, double? AtSeconds);
public sealed record TranscriptUploadRequest(List<TranscriptSegmentInput>? Segments);
public sealed record LessonQualityReviewInput(
    int? TeachingClarity,
    int? StudentPractice,
    int? CorrectionQuality,
    int? Engagement,
    string? Strengths,
    string? Gaps,
    string? TeacherAction,
    string? StudentAction,
    string? Status);

public sealed record UserDto(Guid Id, string FullName, string Email, string Role, bool IsActive, [property: JsonPropertyName("full_name")] string FullNameSnake, [property: JsonPropertyName("is_active")] bool IsActiveSnake, string Name, string? PhoneNumber, string? AvatarUrl);
public sealed record AssignmentDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset CreatedAt, bool IsActive, string Status);
public sealed record AvailabilityDto(Guid Id, Guid TeacherId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
public sealed record TeacherAvailabilityDto(UserDto Teacher, IReadOnlyList<AvailabilityDto> Slots, double? AverageRating = null, int ReviewCount = 0, IReadOnlyList<TeacherReviewDto>? Reviews = null);
public sealed record WeeklyTeacherDto(UserDto Teacher, IReadOnlyList<WeeklyRuleDto> Slots, double? AverageRating = null, int ReviewCount = 0);
public sealed record LessonRequestDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, Guid? AvailabilitySlotId, Guid? StudentAvailabilityId, string InitiatedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset CreatedAt, string? RejectionReason);
public sealed record LessonReportDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, string ClassSummary, string StudentFeedback, string Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TeacherReviewDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int Rating, string? Comment, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PublicTeacherDto(Guid Id, string FullName, string? AvatarUrl, double? AverageRating, int ReviewCount, IReadOnlyList<TeacherReviewDto> Reviews);
public sealed record LessonDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset? ActualStartedAt, DateTimeOffset? ActualEndedAt, LessonReportDto? Report, TeacherReviewDto? Review, Guid? SeriesId = null, bool IsTrial = false);
public sealed record WeeklyRuleDto(Guid Id, Guid TeacherId, int DayOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId, string Status);
public sealed record LessonSeriesDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int DayOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId, bool IsActive, DateTimeOffset CreatedAt);
public sealed record WeeklyAvailabilityDto(UserDto? Teacher, IReadOnlyList<WeeklyRuleDto> Slots, IReadOnlyList<LessonSeriesDto> Series, double? AverageRating = null, int ReviewCount = 0, IReadOnlyList<TeacherReviewDto>? Reviews = null, bool HasLessons = false);
public sealed record LessonChangeRequestDto(Guid Id, Guid SeriesId, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int CurrentDayOfWeek, string CurrentStartsAtTime, string CurrentEndsAtTime, int RequestedDayOfWeek, string RequestedStartsAtTime, string RequestedEndsAtTime, string TimeZoneId, string Status, string? StudentNote, string? AdminNote, DateTimeOffset CreatedAt, DateTimeOffset? AdminRespondedAt);
public sealed record TeacherApplicationDto(Guid Id, string FullName, string Email, string PhoneNumber, IReadOnlyList<string> Subjects, string EnglishLevel, int ExperienceYears, string Bio, string AvailabilityNote, string Status, string? RejectionReason, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, Guid? TeacherId, string? CvFileName, string? IntroFileName, string? QuranRecordingFileName);

public sealed record NotificationDto(Guid Id, string Title, string Body, string Kind, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record NotificationsDto(IReadOnlyList<NotificationDto> Items, int Unread);

public sealed record AcademyBankAccountDto(int Id, string BankName, string AccountHolder, string AccountNumber, string Iban, string Swift, string Notes, DateTimeOffset UpdatedAt);
public sealed record UpdateAcademyBankAccountRequest(string? BankName, string? AccountHolder, string? AccountNumber, string? Iban, string? Swift, string? Notes);
public sealed record TeacherBillingRateDto(Guid TeacherId, string TeacherName, string TeacherEmail, decimal StudentPriceUsd, decimal PayoutEgp);
public sealed record UpdateTeacherBillingRateRequest(decimal StudentPriceUsd, decimal PayoutEgp);
public sealed record TeacherPayoutDetailDto(Guid TeacherId, string InstaPayAccount, string VodafoneCashNumber, string BankName, string BankAccountHolder, string BankIban, DateTimeOffset UpdatedAt);
public sealed record UpdateTeacherPayoutDetailRequest(string? InstaPayAccount, string? VodafoneCashNumber, string? BankName, string? BankAccountHolder, string? BankIban);
public sealed record BillingLessonDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsTrial, decimal Amount);
public sealed record BillingMonthDto(string Month, int LessonCount, decimal Total);
public sealed record MonthlyPaymentClaimDto(Guid Id, Guid StudentId, string StudentName, string StudentEmail, string Month, int LessonCount, decimal AmountUsd, string Status, string? StudentNote, string? AdminNote, DateTimeOffset ClaimedAt, DateTimeOffset? ReviewedAt);
public sealed record StudentBillingDto(string Month, bool MonthClosed, IReadOnlyList<BillingMonthDto> Months, IReadOnlyList<BillingLessonDto> Lessons, decimal TotalUsd, AcademyBankAccountDto? BankAccount, MonthlyPaymentClaimDto? Claim, bool CanClaim);
public sealed record TeacherBillingDto(string Month, bool MonthClosed, IReadOnlyList<BillingMonthDto> Months, IReadOnlyList<BillingLessonDto> Lessons, decimal TotalEgp, decimal PayoutPerLessonEgp, TeacherPayoutDetailDto PayoutDetails, bool HasPayoutDetails);
public sealed record AdminTeacherPayoutDto(Guid TeacherId, string TeacherName, string TeacherEmail, int LessonCount, decimal PayoutPerLessonEgp, decimal TotalEgp, TeacherPayoutDetailDto PayoutDetails);
public sealed record AdminBillingOverviewDto(AcademyBankAccountDto? BankAccount, IReadOnlyList<TeacherBillingRateDto> Rates, IReadOnlyList<MonthlyPaymentClaimDto> Claims, IReadOnlyList<AdminTeacherPayoutDto> TeacherPayouts, string Month);
public sealed record CreatePaymentClaimRequest(string? Month, string? Note);
public sealed record ReviewPaymentClaimRequest(string? Note);
