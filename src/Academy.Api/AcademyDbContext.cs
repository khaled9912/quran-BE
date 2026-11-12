using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed class AcademyDbContext(DbContextOptions<AcademyDbContext> options, IHttpContextAccessor http)
    : IdentityDbContext<AcademyUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    // Every audit row carries who, from where and under which request, so an entry
    // can be lined up with the hosting access log during an incident. Rows written
    // outside a request (the lesson monitor) keep the values the caller set.
    private void StampAuditTrail()
    {
        var context = http.HttpContext;
        if (context is null) return;
        var role = context.User?.FindFirst(ClaimTypes.Role)?.Value;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var correlation = Activity.Current?.Id ?? context.TraceIdentifier;
        foreach (var entry in ChangeTracker.Entries<AuditLog>())
        {
            if (entry.State != EntityState.Added) continue;
            var row = entry.Entity;
            row.ActorRole ??= role;
            row.SourceIp ??= ip;
            row.CorrelationId ??= correlation;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditTrail();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditTrail();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<TeacherStudentAssignment> Assignments => Set<TeacherStudentAssignment>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<StudentAvailability> StudentAvailabilities => Set<StudentAvailability>();
    public DbSet<StudentOnboarding> StudentOnboardings => Set<StudentOnboarding>();
    public DbSet<StudentWeeklyPreference> StudentWeeklyPreferences => Set<StudentWeeklyPreference>();
    public DbSet<OpenStudentOffer> OpenStudentOffers => Set<OpenStudentOffer>();
    public DbSet<LessonRequest> LessonRequests => Set<LessonRequest>();
    public DbSet<TeacherAvailabilityRule> TeacherAvailabilityRules => Set<TeacherAvailabilityRule>();
    public DbSet<LessonSeries> LessonSeries => Set<LessonSeries>();
    public DbSet<LessonChangeRequest> LessonChangeRequests => Set<LessonChangeRequest>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<ContactSubmission> ContactSubmissions => Set<ContactSubmission>();
    public DbSet<LessonTranscriptSegment> LessonTranscriptSegments => Set<LessonTranscriptSegment>();
    public DbSet<LessonTranscriptCapture> LessonTranscriptCaptures => Set<LessonTranscriptCapture>();
    public DbSet<LessonQualityReview> LessonQualityReviews => Set<LessonQualityReview>();
    public DbSet<TranscriptAccessLog> TranscriptAccessLogs => Set<TranscriptAccessLog>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<LessonReport> LessonReports => Set<LessonReport>();
    public DbSet<TeacherReview> TeacherReviews => Set<TeacherReview>();
    public DbSet<TeacherApplication> TeacherApplications => Set<TeacherApplication>();
    public DbSet<TeacherApplicationCv> TeacherApplicationCvs => Set<TeacherApplicationCv>();
    public DbSet<TeacherApplicationIntro> TeacherApplicationIntros => Set<TeacherApplicationIntro>();
    public DbSet<TeacherApplicationQuranRecording> TeacherApplicationQuranRecordings => Set<TeacherApplicationQuranRecording>();
    public DbSet<TeacherBillingRate> TeacherBillingRates => Set<TeacherBillingRate>();
    public DbSet<TeacherPayoutDetail> TeacherPayoutDetails => Set<TeacherPayoutDetail>();
    public DbSet<AcademyBankAccount> AcademyBankAccounts => Set<AcademyBankAccount>();
    public DbSet<MonthlyPaymentClaim> MonthlyPaymentClaims => Set<MonthlyPaymentClaim>();
    public DbSet<AppNotification> Notifications => Set<AppNotification>();
    public DbSet<AcademyPolicy> AcademyPolicies => Set<AcademyPolicy>();
    public DbSet<LessonCancellation> LessonCancellations => Set<LessonCancellation>();
    public DbSet<LessonRescheduleRequest> LessonRescheduleRequests => Set<LessonRescheduleRequest>();
    public DbSet<LessonScheduleChange> LessonScheduleChanges => Set<LessonScheduleChange>();
    public DbSet<LessonAttendance> LessonAttendances => Set<LessonAttendance>();
    public DbSet<LessonAttendanceEvent> LessonAttendanceEvents => Set<LessonAttendanceEvent>();
    public DbSet<LessonAttendanceInterval> LessonAttendanceIntervals => Set<LessonAttendanceInterval>();
    public DbSet<LessonCreditTransaction> LessonCreditTransactions => Set<LessonCreditTransaction>();
    public DbSet<TeacherHours> TeacherHours => Set<TeacherHours>();
    public DbSet<TeacherPenalty> TeacherPenalties => Set<TeacherPenalty>();
    public DbSet<ReliabilityEvent> ReliabilityEvents => Set<ReliabilityEvent>();
    public DbSet<SalaryPeriod> SalaryPeriods => Set<SalaryPeriod>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LessonExcuseRequest> LessonExcuseRequests => Set<LessonExcuseRequest>();
    public DbSet<LessonIncident> LessonIncidents => Set<LessonIncident>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<BrowserSubscription> BrowserSubscriptions => Set<BrowserSubscription>();
    public DbSet<LessonCall> LessonCalls => Set<LessonCall>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<LessonTranscriptSegment>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonTranscriptSegment>().HasIndex(x => new { x.LessonId, x.CreatedAt });
        b.Entity<LessonTranscriptSegment>().HasIndex(x => x.Flags).HasFilter("\"Flags\" <> ''");
        b.Entity<LessonTranscriptSegment>().HasIndex(x => x.CreatedAt);
        b.Entity<LessonTranscriptSegment>().HasIndex(x => new { x.LessonId, x.SourceUserId, x.ClientSegmentId }).IsUnique();
        // Read boundaries still expire text while the host is asleep between cleanup runs.
        b.Entity<LessonTranscriptSegment>().HasQueryFilter(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30));
        b.Entity<LessonTranscriptCapture>().HasKey(x => new { x.LessonId, x.UserId });
        b.Entity<LessonTranscriptCapture>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonTranscriptCapture>().HasIndex(x => x.FirstSeenAt);
        b.Entity<LessonTranscriptCapture>().HasQueryFilter(x => x.FirstSeenAt >= DateTimeOffset.UtcNow.AddDays(-30));
        b.Entity<LessonQualityReview>().HasOne(x => x.Lesson).WithOne().HasForeignKey<LessonQualityReview>(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonQualityReview>().HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonQualityReview>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<LessonQualityReview>().HasIndex(x => new { x.Status, x.UpdatedAt });
        b.Entity<TranscriptAccessLog>().HasOne<Lesson>().WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TranscriptAccessLog>().HasIndex(x => new { x.LessonId, x.AccessedAt });
        b.Entity<TranscriptAccessLog>().HasIndex(x => x.UserId);
        b.Entity<TeacherStudentAssignment>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherStudentAssignment>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherStudentAssignment>().HasIndex(x => new { x.TeacherId, x.StudentId }).IsUnique().HasFilter("\"IsActive\" = TRUE");
        b.Entity<TeacherStudentAssignment>().HasIndex(x => x.StudentId).IsUnique().HasFilter("\"IsActive\" = TRUE");
        b.Entity<AvailabilitySlot>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AvailabilitySlot>().HasIndex(x => new { x.TeacherId, x.StartsAt, x.EndsAt });
        b.Entity<StudentAvailability>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StudentAvailability>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StudentAvailability>().HasIndex(x => new { x.StudentId, x.StartsAt, x.EndsAt });
        b.Entity<StudentOnboarding>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StudentOnboarding>().HasIndex(x => x.StudentId).IsUnique();
        b.Entity<StudentWeeklyPreference>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StudentWeeklyPreference>().HasIndex(x => new { x.StudentId, x.DayOfWeek, x.StartsAtTime, x.EndsAtTime }).IsUnique();
        b.Entity<OpenStudentOffer>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OpenStudentOffer>().HasOne(x => x.ClaimedByTeacher).WithMany().HasForeignKey(x => x.ClaimedByTeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OpenStudentOffer>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<OpenStudentOffer>().HasIndex(x => new { x.StudentId, x.StartsAt });
        b.Entity<OpenStudentOffer>().HasIndex(x => new { x.Status, x.StartsAt });
        b.Entity<OpenStudentOffer>().HasIndex(x => new { x.Status, x.ClaimedAt });
        b.Entity<LessonRequest>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.AvailabilitySlot).WithMany().HasForeignKey(x => x.AvailabilitySlotId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasOne(x => x.StudentAvailability).WithMany().HasForeignKey(x => x.StudentAvailabilityId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonRequest>().HasIndex(x => x.StudentId).IsUnique().HasFilter("\"Status\" IN ('pending_teacher','pending_student','pending_admin')");
        b.Entity<LessonRequest>().HasIndex(x => x.AvailabilitySlotId).IsUnique().HasFilter("\"AvailabilitySlotId\" IS NOT NULL AND \"Status\" IN ('pending_teacher','pending_admin','approved')");
        b.Entity<LessonRequest>().HasIndex(x => x.StudentAvailabilityId).IsUnique().HasFilter("\"StudentAvailabilityId\" IS NOT NULL AND \"Status\" IN ('pending_student','pending_admin','approved')");
        b.Entity<TeacherAvailabilityRule>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherAvailabilityRule>().HasIndex(x => new { x.TeacherId, x.DayOfWeek, x.StartsAtTime, x.EndsAtTime }).IsUnique();
        b.Entity<LessonSeries>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonSeries>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonSeries>().HasOne(x => x.AvailabilityRule).WithMany().HasForeignKey(x => x.AvailabilityRuleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonSeries>().HasIndex(x => x.AvailabilityRuleId).IsUnique().HasFilter("\"IsActive\" = TRUE");
        b.Entity<LessonSeries>().HasIndex(x => new { x.TeacherId, x.DayOfWeek, x.StartsAtTime });
        b.Entity<LessonSeries>().HasIndex(x => new { x.StudentId, x.IsActive });
        b.Entity<LessonChangeRequest>().HasOne(x => x.Series).WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonChangeRequest>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonChangeRequest>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonChangeRequest>().HasOne(x => x.RequestedRule).WithMany().HasForeignKey(x => x.RequestedRuleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonChangeRequest>().HasIndex(x => x.SeriesId).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.Entity<LessonChangeRequest>().HasIndex(x => x.Status);
        b.Entity<Lesson>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasOne(x => x.LessonRequest).WithMany().HasForeignKey(x => x.LessonRequestId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Lesson>().HasOne(x => x.Series).WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Lesson>().HasIndex(x => x.LessonRequestId).IsUnique();
        b.Entity<Lesson>().HasIndex(x => new { x.SeriesId, x.StartsAt })
            .HasDatabaseName("IX_Lessons_SeriesId_StartsAt_Current").IsUnique()
            .HasFilter("\"SeriesId\" IS NOT NULL AND (\"CancellationOutcome\" IS NULL OR \"CancellationOutcome\" <> 'series_rescheduled')");
        b.Entity<Lesson>().HasIndex(x => new { x.TeacherId, x.StartsAt, x.EndsAt });
        b.Entity<Lesson>().HasIndex(x => new { x.StudentId, x.StartsAt, x.EndsAt });
        b.Entity<AppNotification>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AppNotification>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<AppNotification>().HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        // Broadcast notifications are updated by dedupe key across every recipient.
        b.Entity<AppNotification>().HasIndex(x => x.DedupeKey).HasFilter("\"DedupeKey\" IS NOT NULL");
        b.Entity<AppNotification>().HasIndex(x => new { x.UserId, x.ReadAt });
        b.Entity<UserAvatar>().ToTable("UserAvatars");
        b.Entity<UserAvatar>().HasKey(x => x.UserId);
        b.Entity<UserAvatar>().HasOne(x => x.User).WithOne().HasForeignKey<UserAvatar>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserAvatar>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<UserAvatar>().Property(x => x.Data).IsRequired();
        b.Entity<LessonReport>().HasOne(x => x.Lesson).WithOne(x => x.Report).HasForeignKey<LessonReport>(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonReport>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonReport>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonReport>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<LessonReport>().HasIndex(x => x.TeacherId);
        b.Entity<LessonReport>().HasIndex(x => x.StudentId);
        b.Entity<TeacherReview>().HasOne(x => x.Lesson).WithOne(x => x.Review).HasForeignKey<TeacherReview>(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherReview>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherReview>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherReview>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<TeacherReview>().HasIndex(x => x.TeacherId);
        b.Entity<TeacherApplication>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<TeacherApplication>().HasIndex(x => x.Email).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.Entity<TeacherApplicationCv>().ToTable("TeacherApplicationCvs");
        b.Entity<TeacherApplicationCv>().HasKey(x => x.ApplicationId);
        b.Entity<TeacherApplicationCv>().HasOne(x => x.Application).WithOne(x => x.Cv).HasForeignKey<TeacherApplicationCv>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherApplicationCv>().Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Entity<TeacherApplicationCv>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<TeacherApplicationCv>().Property(x => x.Data).IsRequired();
        b.Entity<TeacherApplicationIntro>().ToTable("TeacherApplicationIntros");
        b.Entity<TeacherApplicationIntro>().HasKey(x => x.ApplicationId);
        b.Entity<TeacherApplicationIntro>().HasOne(x => x.Application).WithOne(x => x.Intro).HasForeignKey<TeacherApplicationIntro>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherApplicationIntro>().Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Entity<TeacherApplicationIntro>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<TeacherApplicationIntro>().Property(x => x.Data).IsRequired();
        b.Entity<TeacherApplicationQuranRecording>().ToTable("TeacherApplicationQuranRecordings");
        b.Entity<TeacherApplicationQuranRecording>().HasKey(x => x.ApplicationId);
        b.Entity<TeacherApplicationQuranRecording>().HasOne(x => x.Application).WithOne(x => x.QuranRecording).HasForeignKey<TeacherApplicationQuranRecording>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherApplicationQuranRecording>().Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Entity<TeacherApplicationQuranRecording>().Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Entity<TeacherApplicationQuranRecording>().Property(x => x.Data).IsRequired();
        b.Entity<TeacherBillingRate>().HasKey(x => x.TeacherId);
        b.Entity<TeacherBillingRate>().HasOne(x => x.Teacher).WithOne().HasForeignKey<TeacherBillingRate>(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherBillingRate>().Property(x => x.StudentPriceUsd).HasPrecision(10, 2);
        b.Entity<TeacherBillingRate>().Property(x => x.PayoutEgp).HasPrecision(10, 2);
        b.Entity<TeacherPayoutDetail>().HasKey(x => x.TeacherId);
        b.Entity<TeacherPayoutDetail>().HasOne(x => x.Teacher).WithOne().HasForeignKey<TeacherPayoutDetail>(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AcademyBankAccount>().HasKey(x => x.Id);
        b.Entity<MonthlyPaymentClaim>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MonthlyPaymentClaim>().Property(x => x.AmountUsd).HasPrecision(10, 2);
        b.Entity<MonthlyPaymentClaim>().HasIndex(x => new { x.StudentId, x.Month }).IsUnique();
        b.Entity<MonthlyPaymentClaim>().HasIndex(x => x.Status);

        b.Entity<AcademyPolicy>().HasKey(x => x.Id);
        b.Entity<LessonCancellation>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonCancellation>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<LessonCancellation>().HasIndex(x => new { x.Outcome, x.CreatedAt });
        b.Entity<LessonRescheduleRequest>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonRescheduleRequest>().HasIndex(x => x.LessonId).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.Entity<LessonRescheduleRequest>().HasIndex(x => new { x.Status, x.ExpiresAt });
        b.Entity<LessonScheduleChange>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonScheduleChange>().HasIndex(x => new { x.LessonId, x.CreatedAt });
        b.Entity<LessonAttendance>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonAttendance>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonAttendance>().HasIndex(x => new { x.LessonId, x.UserId }).IsUnique();
        b.Entity<LessonAttendance>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<LessonAttendanceEvent>().HasIndex(x => new { x.UserId, x.EventId }).IsUnique();
        b.Entity<LessonAttendanceEvent>().HasIndex(x => x.LessonId);
        b.Entity<LessonAttendanceInterval>().HasOne(x => x.Attendance).WithMany().HasForeignKey(x => x.AttendanceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonAttendanceInterval>().HasIndex(x => x.LessonId);
        b.Entity<LessonAttendanceInterval>().HasIndex(x => new { x.LessonId, x.UserId, x.DeviceId }).IsUnique().HasFilter("\"LeftAt\" IS NULL");
        b.Entity<LessonCreditTransaction>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonCreditTransaction>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<LessonCreditTransaction>().HasIndex(x => new { x.StudentId, x.CreatedAt });
        b.Entity<LessonCreditTransaction>().HasIndex(x => x.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        b.Entity<TeacherHours>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TeacherHours>().HasIndex(x => x.LessonId).IsUnique();
        b.Entity<TeacherHours>().HasIndex(x => new { x.TeacherId, x.CreatedAt });
        b.Entity<TeacherHours>().HasIndex(x => x.ApprovalStatus);
        b.Entity<TeacherPenalty>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeacherPenalty>().Property(x => x.AmountEgp).HasPrecision(10, 2);
        b.Entity<TeacherPenalty>().HasIndex(x => new { x.TeacherId, x.Status });
        b.Entity<ReliabilityEvent>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReliabilityEvent>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<ReliabilityEvent>().HasIndex(x => new { x.Role, x.Type, x.CreatedAt });
        b.Entity<SalaryPeriod>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SalaryPeriod>().Property(x => x.ApprovedPenaltyEgp).HasPrecision(10, 2);
        b.Entity<SalaryPeriod>().Property(x => x.PendingPenaltyEgp).HasPrecision(10, 2);
        b.Entity<SalaryPeriod>().Property(x => x.ManualAdjustmentEgp).HasPrecision(10, 2);
        b.Entity<SalaryPeriod>().Property(x => x.FinalAmountEgp).HasPrecision(10, 2);
        b.Entity<SalaryPeriod>().HasIndex(x => new { x.TeacherId, x.Month }).IsUnique();
        b.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt });
        b.Entity<AuditLog>().HasIndex(x => new { x.ActorId, x.CreatedAt });
        b.Entity<AuditLog>().HasIndex(x => x.CorrelationId);
        b.Entity<LessonExcuseRequest>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonExcuseRequest>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonExcuseRequest>().HasIndex(x => x.LessonId).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.Entity<LessonExcuseRequest>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<LessonIncident>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonIncident>().HasIndex(x => new { x.LessonId, x.Type }).IsUnique();
        b.Entity<LessonIncident>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<DeviceToken>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeviceToken>().HasIndex(x => x.Token).IsUnique();
        b.Entity<DeviceToken>().HasIndex(x => x.UserId);
        b.Entity<BrowserSubscription>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BrowserSubscription>().HasIndex(x => x.Endpoint).IsUnique();
        b.Entity<BrowserSubscription>().HasIndex(x => x.UserId);
        b.Entity<LessonCall>().HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonCall>().HasOne(x => x.Caller).WithMany().HasForeignKey(x => x.CallerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonCall>().HasOne(x => x.Callee).WithMany().HasForeignKey(x => x.CalleeId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonCall>().HasIndex(x => new { x.LessonId, x.CreatedAt });
        b.Entity<LessonCall>().HasIndex(x => new { x.CalleeId, x.CreatedAt });
    }
}
