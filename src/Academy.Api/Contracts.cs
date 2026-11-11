using System.Text.Json.Serialization;

namespace Academy.Api;

public sealed record RegisterRequest(string FullName, string Email, string Password, string? Country);
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequestInput(string Email);
public sealed record PasswordResetInput(string Email, string Code, string NewPassword);
public sealed record OtpRequest(string Phone);
public sealed record OtpVerifyRequest(string Phone, string Code, string? FullName, string? Country);
public sealed record CreateUserRequest(string FullName, string Email, string Password, string Role);
public sealed record CreateStudentRequest(string FullName, string Email, string Password, string? Grade);
public sealed record UpdateUserRequest(string? FullName, string? Email, string? Password, bool? IsActive);
public sealed record UpdateProfileRequest(string? FullName, string? Email, string? PhoneNumber, string? CurrentPassword, string? PhoneCode);
public sealed record UpdateLessonRequest(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Status);
public sealed record AssignStudentRequest(Guid TeacherId, Guid StudentId);
public sealed record PublishAvailabilityRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record ContactRequest(string Name, string Email, string Message);
public sealed record RejectRequest(string? Reason);
public sealed record RequestSlotInput(Guid AvailabilitySlotId);
public sealed record PublishOpenOfferRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record CreateAvailabilityRulesRequest(List<int>? DaysOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId);
public sealed record BookWeeklySlotsRequest(List<Guid>? RuleIds);
public sealed record CreateLessonChangeRequestInput(Guid RequestedRuleId, string? Note);
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

public sealed record UserDto(Guid Id, string? FullName, string Email, string Role, bool IsActive, [property: JsonPropertyName("full_name")] string? FullNameSnake, [property: JsonPropertyName("is_active")] bool IsActiveSnake, string? Name, string? PhoneNumber, string? AvatarUrl, string? Grade = null);
public sealed record AssignmentDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset CreatedAt, bool IsActive, string Status);
public sealed record AvailabilityDto(Guid Id, Guid TeacherId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
public sealed record OpenStudentOfferDto(Guid Id, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, Guid? ClaimedByTeacherId, string? ClaimedByTeacherName, Guid? LessonId, DateTimeOffset CreatedAt, DateTimeOffset? ClaimedAt);
public sealed record TeacherAvailabilityDto(UserDto Teacher, IReadOnlyList<AvailabilityDto> Slots, double? AverageRating = null, int ReviewCount = 0, IReadOnlyList<TeacherReviewDto>? Reviews = null);
public sealed record WeeklyTeacherDto(UserDto Teacher, IReadOnlyList<WeeklyRuleDto> Slots, double? AverageRating = null, int ReviewCount = 0);
public sealed record LessonRequestDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, Guid? AvailabilitySlotId, Guid? StudentAvailabilityId, string InitiatedBy, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset CreatedAt, string? RejectionReason);
public sealed record LessonReportDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, string ClassSummary, string StudentFeedback, string Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TeacherReviewDto(Guid Id, Guid LessonId, DateTimeOffset StartsAt, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int Rating, string? Comment, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PublicTeacherDto(Guid Id, string FullName, string? AvatarUrl, double? AverageRating, int ReviewCount, IReadOnlyList<TeacherReviewDto> Reviews);
public sealed record LessonDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset? ActualStartedAt, DateTimeOffset? ActualEndedAt, LessonReportDto? Report, TeacherReviewDto? Review, Guid? SeriesId = null, bool IsTrial = false, string? CancellationOutcome = null, string? CancelReason = null, DateTimeOffset? CancelledAt = null, Guid? CancelledById = null, bool IsMakeup = false, Guid? OriginalLessonId = null);
public sealed record WeeklyRuleDto(Guid Id, Guid TeacherId, int DayOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId, string Status, DateTimeOffset? NextStartsAt = null);
public sealed record LessonSeriesDto(Guid Id, Guid TeacherId, string TeacherName, Guid StudentId, string StudentName, int DayOfWeek, string StartsAtTime, string EndsAtTime, string TimeZoneId, bool IsActive, DateTimeOffset CreatedAt);
public sealed record WeeklyAvailabilityDto(UserDto? Teacher, IReadOnlyList<WeeklyRuleDto> Slots, IReadOnlyList<LessonSeriesDto> Series, double? AverageRating = null, int ReviewCount = 0, IReadOnlyList<TeacherReviewDto>? Reviews = null, bool HasLessons = false, int TimeChangeLeadMinutes = 0);
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

// Lesson operations: cancellation, rescheduling, attendance, hours, reliability.
public sealed record AcademyPolicyDto(
    int StudentFreeCancelHours,
    int TeacherFreeCancelHours,
    int NoShowGraceMinutes,
    int RescheduleResponseHours,
    int StudentGraceCancellationsPerMonth,
    int StudentWarningStrikeThreshold,
    int StudentAdminReviewStrikeThreshold,
    int TeacherAdminReviewStrikeThreshold,
    int TrialNoShowThreshold,
    bool PayScheduledDurationWhenAttended,
    DateTimeOffset UpdatedAt);

public sealed record UpdateAcademyPolicyRequest(
    int? StudentFreeCancelHours,
    int? TeacherFreeCancelHours,
    int? NoShowGraceMinutes,
    int? RescheduleResponseHours,
    int? StudentGraceCancellationsPerMonth,
    int? StudentWarningStrikeThreshold,
    int? StudentAdminReviewStrikeThreshold,
    int? TeacherAdminReviewStrikeThreshold,
    int? TrialNoShowThreshold,
    bool? PayScheduledDurationWhenAttended);

public sealed record CancellationPreviewDto(
    bool Allowed,
    string? BlockedReason,
    string ActorRole,
    string Outcome,
    bool IsLate,
    bool UsedGrace,
    int GraceUsedThisMonth,
    int GraceLimit,
    double HoursBefore,
    int ScheduledMinutes,
    int TeacherPayableMinutes,
    int CreditChange,
    // none | deduct | restore
    string CreditEffect,
    string? ReliabilityEvent,
    bool RequiresAdminReview,
    string Explanation,
    string? ExistingOutcome,
    AcademyPolicyDto Policy);

public sealed record CancelLessonRequest(string? Reason);

public sealed record LessonCancellationDto(
    Guid Id,
    Guid LessonId,
    string ActorRole,
    string Outcome,
    bool IsLate,
    bool UsedGrace,
    double HoursBefore,
    int TeacherPayableMinutes,
    string? Reason,
    DateTimeOffset CreatedAt);

public sealed record CreateRescheduleRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Note);
public sealed record RespondRescheduleRequest(string? Note);

public sealed record RescheduleRequestDto(
    Guid Id,
    Guid LessonId,
    Guid RequestedById,
    string RequestedByName,
    string RequestedByRole,
    DateTimeOffset ProposedStartsAt,
    DateTimeOffset ProposedEndsAt,
    string? Note,
    string Status,
    DateTimeOffset ExpiresAt,
    Guid? RespondedById,
    DateTimeOffset? RespondedAt,
    string? ResponseNote,
    DateTimeOffset CreatedAt,
    int MinutesRemaining);

public sealed record CreateReplacementRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Note);

public sealed record AttendanceBeatRequest(string? DeviceId, string? EventId, string Action, DateTimeOffset? ClientAt);

public sealed record AttendanceParticipantDto(
    Guid UserId,
    string Name,
    string Role,
    string Status,
    DateTimeOffset? FirstJoinedAt,
    DateTimeOffset? LastLeftAt,
    int TotalSeconds,
    int ReconnectionCount,
    bool JoinedLate,
    bool LeftEarly);

public sealed record AttendanceIntervalDto(
    Guid Id,
    string DeviceId,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    int Seconds);

public sealed record AttendanceSummaryDto(
    Guid LessonId,
    string LessonStatus,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset ServerNow,
    IReadOnlyList<AttendanceParticipantDto> Participants);

public sealed record AttendanceBeatResultDto(
    bool Accepted,
    string ParticipantStatus,
    int TotalSeconds,
    DateTimeOffset ServerAt,
    AttendanceSummaryDto Summary);

public sealed record CreateExcuseRequest(string? Category, string? Explanation);
public sealed record ExcuseReviewRequest(string? Note);

public sealed record ExcuseRequestDto(
    Guid Id,
    Guid LessonId,
    Guid StudentId,
    string StudentName,
    DateTimeOffset StartsAt,
    string Category,
    string Explanation,
    string Status,
    string? AdminNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt);

public sealed record ResolveIncidentRequest(string Action, string? Note, int? TeacherPayableMinutes);
public sealed record ReportIncidentRequest(string? Type, string? Detail);

public sealed record LessonIncidentDto(
    Guid Id,
    Guid LessonId,
    DateTimeOffset StartsAt,
    Guid TeacherId,
    string TeacherName,
    Guid StudentId,
    string StudentName,
    string Type,
    string Status,
    string Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

public sealed record TeacherHoursDto(
    Guid Id,
    Guid LessonId,
    Guid TeacherId,
    string TeacherName,
    Guid StudentId,
    string StudentName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string LessonStatus,
    int ScheduledMinutes,
    int ActualAttendanceMinutes,
    int ProposedPayableMinutes,
    int? ApprovedPayableMinutes,
    string ApprovalStatus,
    string? AdjustmentReason,
    DateTimeOffset? ApprovedAt);

public sealed record ApproveTeacherHoursRequest(int? ApprovedMinutes, string? Note);

public sealed record CreateTeacherPenaltyRequest(Guid TeacherId, Guid? LessonId, string? EventType, decimal AmountEgp, string Reason);
public sealed record ReviewTeacherPenaltyRequest(string Status, string? Note);

public sealed record TeacherPenaltyDto(
    Guid Id,
    Guid TeacherId,
    string TeacherName,
    Guid? LessonId,
    string EventType,
    decimal AmountEgp,
    string Reason,
    string Status,
    string? AdminNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt);

public sealed record ReliabilityEventDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string Role,
    Guid? LessonId,
    string Type,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote,
    string? ResolutionAction);

public sealed record ResolveReliabilityRequest(string? Note, string? Action);

public sealed record LessonCreditTransactionDto(
    Guid Id,
    Guid StudentId,
    Guid? LessonId,
    string Type,
    int Amount,
    string Reason,
    string CreatedByRole,
    DateTimeOffset CreatedAt);

public sealed record StudentCreditDto(Guid StudentId, int Balance, IReadOnlyList<LessonCreditTransactionDto> Transactions);

public sealed record AdjustCreditRequest(int Amount, string Reason);

public sealed record SalaryPeriodDto(
    Guid Id,
    Guid TeacherId,
    string TeacherName,
    string Month,
    string Status,
    int TotalScheduledMinutes,
    int TotalActualMinutes,
    int TotalProposedMinutes,
    int TotalApprovedMinutes,
    decimal ApprovedPenaltyEgp,
    decimal PendingPenaltyEgp,
    int CompletedLessons,
    int StudentLateCancellations,
    int TeacherCancellations,
    int TeacherNoShows,
    int StudentNoShows,
    decimal ManualAdjustmentEgp,
    decimal? FinalAmountEgp,
    string? Notes,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? PaidAt);

public sealed record GenerateSalaryPeriodsRequest(string? Month);
public sealed record UpdateSalaryPeriodRequest(decimal? ManualAdjustmentEgp, decimal? FinalAmountEgp, string? Notes, string? Status);

public sealed record RegisterDeviceTokenRequest(string? Token, string? Platform);

/// <summary>
/// A browser's Web Push subscription: the queue the push service gave it, plus
/// the two keys needed to seal a message for that queue alone.
/// </summary>
public sealed record WebPushSubscriptionRequest(string? Endpoint, string? P256Dh, string? Auth);

/// <summary>Ring the other participant of a live lesson. Staff may name a side.</summary>
public sealed record CallLessonRequest(string? TargetRole);

public sealed record LessonCallDto(
    Guid Id,
    Guid LessonId,
    Guid CallerId,
    string CallerName,
    string CallerRole,
    Guid CalleeId,
    string CalleeName,
    string CalleeRole,
    string Origin,
    string CalleeStatus,
    DateTimeOffset CreatedAt);

public sealed record LessonOperationsDto(
    Guid LessonId,
    CancellationPreviewDto Cancellation,
    LessonCancellationDto? CancellationRecord,
    RescheduleRequestDto? PendingReschedule,
    IReadOnlyList<RescheduleRequestDto> RescheduleHistory,
    AttendanceSummaryDto Attendance,
    TeacherHoursDto? Hours,
    ExcuseRequestDto? Excuse,
    LessonIncidentDto? Incident,
    LessonDto? ReplacementLesson,
    LessonDto? OriginalLesson,
    IReadOnlyList<LessonCallDto> Calls);

public sealed record LessonOpsSummaryDto(
    int Lessons,
    int LiveNow,
    int Upcoming,
    int PendingReports,
    int MissingAttendance,
    int PendingReschedules,
    int PendingExcuses,
    int NoShowIncidents,
    int PendingHours);

public sealed record LessonOpsLessonDto(
    LessonDto Lesson,
    AttendanceSummaryDto Attendance,
    string? CancellationOutcome,
    RescheduleRequestDto? PendingReschedule,
    TeacherHoursDto? Hours,
    IReadOnlyList<LessonIncidentDto> Incidents,
    bool ReportDue);

public sealed record AdminLessonOpsDto(
    string Date,
    LessonOpsSummaryDto Summary,
    IReadOnlyList<LessonOpsLessonDto> Lessons);

public sealed record AdminLessonOpsDetailDto(
    LessonDto Lesson,
    AttendanceSummaryDto Attendance,
    IReadOnlyList<AttendanceIntervalDto> Intervals,
    LessonCancellationDto? Cancellation,
    IReadOnlyList<RescheduleRequestDto> RescheduleRequests,
    TeacherHoursDto? Hours,
    IReadOnlyList<LessonIncidentDto> Incidents,
    ExcuseRequestDto? Excuse,
    IReadOnlyList<LessonCreditTransactionDto> Credits,
    IReadOnlyList<LessonScheduleChangeDto> ScheduleChanges,
    IReadOnlyList<LessonCallDto> Calls);

public sealed record LessonScheduleChangeDto(
    Guid Id,
    DateTimeOffset FromStartsAt,
    DateTimeOffset FromEndsAt,
    DateTimeOffset ToStartsAt,
    DateTimeOffset ToEndsAt,
    Guid? ChangedById,
    string? Reason,
    DateTimeOffset CreatedAt);
