using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// Monthly statements and manual payments. Students transfer the month's USD
/// balance to the academy account and mark it as sent; teachers keep EGP payout
/// details that admins use for the monthly transfer.
/// </summary>
public static class PaymentEndpoints
{
    private const int BankAccountId = 1;
    private const string MonthFormat = "yyyy-MM";
    private static readonly TimeZoneInfo AcademyTimeZone = ResolveAcademyTimeZone();

    public static void MapPayments(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        var student = api.MapGroup("/student/billing").RequireAuthorization("StudentOnly");
        student.MapGet("", StudentBillingAsync);
        student.MapPost("/claims", CreateClaimAsync);

        var teacher = api.MapGroup("/teacher/billing").RequireAuthorization("TeacherOnly");
        teacher.MapGet("", TeacherBillingAsync);
        teacher.MapPut("/payout-details", SavePayoutDetailsAsync);

        var admin = api.MapGroup("/admin/billing").RequireAuthorization("AdminOnly");
        admin.MapGet("", AdminOverviewAsync);
        admin.MapPut("/academy-account", SaveAcademyAccountAsync);
        admin.MapPut("/rates/{teacherId:guid}", SaveRateAsync);
        admin.MapPost("/claims/{id:guid}/confirm", ConfirmClaimAsync);
        admin.MapPost("/claims/{id:guid}/reject", RejectClaimAsync);
    }

    private static async Task<IResult> StudentBillingAsync(
        string? month,
        ClaimsPrincipal principal,
        AcademyDbContext db)
    {
        var studentId = ApiEndpoints.UserId(principal);
        var completed = await db.Lessons
            .AsNoTracking()
            .Include(x => x.Teacher)
            .Where(x => x.StudentId == studentId && x.Status == "completed")
            .OrderBy(x => x.StartsAt)
            .ToListAsync();

        var rates = await LoadRatesAsync(db, completed.Select(x => x.TeacherId));
        // A student's first lesson is a trial: it appears on the statement but is free.
        var months = BuildMonths(completed.Select(x => (x.StartsAt, StudentAmount(x, rates))));

        var resolved = ResolveMonth(month, months);
        if (resolved is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["Choose a valid month (YYYY-MM)."] });
        var (selected, key) = resolved.Value;

        var (start, end) = MonthBounds(selected);
        var lessons = completed
            .Where(x => x.StartsAt >= start && x.StartsAt < end)
            .Select(x => new BillingLessonDto(
                x.Id,
                x.TeacherId,
                x.Teacher?.FullName ?? "",
                x.StudentId,
                "",
                x.StartsAt,
                x.EndsAt,
                x.IsTrial,
                StudentAmount(x, rates)))
            .ToList();

        var total = lessons.Sum(x => x.Amount);
        var closed = end <= DateTimeOffset.UtcNow;
        var bank = await LoadBankAsync(db);
        var claim = await db.MonthlyPaymentClaims
            .AsNoTracking()
            .Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.Month == key);

        var canClaim = closed
            && total > 0
            && (claim is null || claim.Status == "rejected");

        return Results.Ok(new StudentBillingDto(
            key,
            closed,
            months,
            lessons,
            total,
            bank is null ? null : ToBank(bank),
            claim is null ? null : ToClaim(claim, claim.Student),
            canClaim));
    }

    private static async Task<IResult> CreateClaimAsync(
        CreatePaymentClaimRequest input,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        RealtimeNotifier realtime,
        AcademyPushSender push)
    {
        if (!TryParseMonth(input.Month, out var month))
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["Choose a valid month (YYYY-MM)."] });

        var (start, end) = MonthBounds(month);
        if (end > DateTimeOffset.UtcNow)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["That month has not ended yet."] });

        var studentId = ApiEndpoints.UserId(principal);
        var lessons = await db.Lessons
            .AsNoTracking()
            .Where(x => x.StudentId == studentId
                && x.Status == "completed"
                && x.StartsAt >= start
                && x.StartsAt < end)
            .ToListAsync();

        var rates = await LoadRatesAsync(db, lessons.Select(x => x.TeacherId));
        var total = lessons.Sum(x => StudentAmount(x, rates));
        if (total <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["There is nothing to pay for that month."] });

        var key = MonthKey(month);
        var claim = await db.MonthlyPaymentClaims
            .Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.Month == key);

        if (claim is { Status: "confirmed" })
            return Results.Conflict(new { message = "The academy has already confirmed this month's payment." });
        if (claim is { Status: "pending" })
            return Results.Conflict(new { message = "You already marked this month as paid. The academy will confirm it." });

        if (claim is null)
        {
            claim = new MonthlyPaymentClaim { StudentId = studentId, Month = key };
            db.MonthlyPaymentClaims.Add(claim);
        }

        claim.LessonCount = lessons.Count;
        claim.AmountUsd = total;
        claim.Status = "pending";
        claim.StudentNote = Clean(input.Note, 500);
        claim.ClaimedAt = DateTimeOffset.UtcNow;
        claim.AdminNote = null;
        claim.ReviewedAt = null;
        claim.ReviewedBy = null;
        await db.SaveChangesAsync();

        claim.Student ??= await db.Users.AsNoTracking().FirstAsync(x => x.Id == studentId);
        var adminIds = await (
            from userRole in db.UserRoles
            join roleRow in db.Roles on userRole.RoleId equals roleRow.Id
            where roleRow.Name == "admin" || roleRow.Name == "super_admin"
            select userRole.UserId
        ).Distinct().ToListAsync();
        // One message for every admin: the claim waits on a person, so the
        // reviewers are told on their phones and not only in the feed.
        var reviewTitle = "Payment marked as sent";
        var reviewBody = $"{claim.Student.FullName} marked {key} as paid (${claim.AmountUsd:0.00}). Review the claim when you can.";
        foreach (var adminId in adminIds)
            db.Notifications.Add(new AppNotification
            {
                UserId = adminId,
                Title = reviewTitle,
                Body = reviewBody,
                Kind = "payment",
                Link = "/people",
                DedupeKey = $"payment-claim:{claim.Id}:{claim.ClaimedAt.UtcTicks}",
            });
        if (adminIds.Count > 0) await db.SaveChangesAsync();
        await realtime.ToStaffAsync(RealtimeScopes.Billing);
        await realtime.ToStaffAsync(RealtimeScopes.Admin);
        await realtime.ToUserAsync(studentId, RealtimeScopes.Billing);
        await realtime.ToUsersAsync(adminIds, RealtimeScopes.Notifications);
        if (adminIds.Count > 0)
            await LessonOpsMessaging.PushAsync(db, push, adminIds, reviewTitle, reviewBody, "/people");
        return Results.Ok(ToClaim(claim, claim.Student));
    }

    private static async Task<IResult> TeacherBillingAsync(
        string? month,
        ClaimsPrincipal principal,
        AcademyDbContext db)
    {
        var teacherId = ApiEndpoints.UserId(principal);
        var completed = await db.Lessons
            .AsNoTracking()
            .Include(x => x.Student)
            .Where(x => x.TeacherId == teacherId && x.Status == "completed")
            .OrderBy(x => x.StartsAt)
            .ToListAsync();

        var rate = await db.TeacherBillingRates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TeacherId == teacherId);
        var payout = rate?.PayoutEgp ?? 0m;

        // Trials are free for students but still paid to the teacher.
        var months = BuildMonths(completed.Select(x => (x.StartsAt, payout)));

        var resolved = ResolveMonth(month, months);
        if (resolved is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["Choose a valid month (YYYY-MM)."] });
        var (selected, key) = resolved.Value;

        var (start, end) = MonthBounds(selected);
        var lessons = completed
            .Where(x => x.StartsAt >= start && x.StartsAt < end)
            .Select(x => new BillingLessonDto(
                x.Id,
                x.TeacherId,
                "",
                x.StudentId,
                x.Student?.FullName ?? "",
                x.StartsAt,
                x.EndsAt,
                x.IsTrial,
                payout))
            .ToList();

        var details = await db.TeacherPayoutDetails
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TeacherId == teacherId);

        return Results.Ok(new TeacherBillingDto(
            key,
            end <= DateTimeOffset.UtcNow,
            months,
            lessons,
            lessons.Sum(x => x.Amount),
            payout,
            ToDetails(teacherId, details),
            HasDetails(details)));
    }

    private static async Task<IResult> SavePayoutDetailsAsync(
        UpdateTeacherPayoutDetailRequest input,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        RealtimeNotifier realtime)
    {
        var instapay = Clean(input.InstaPayAccount, 120);
        var vodafone = Clean(input.VodafoneCashNumber, 30);
        var bankName = Clean(input.BankName, 120);
        var holder = Clean(input.BankAccountHolder, 160);
        var iban = Clean(input.BankIban, 64);

        if (instapay is null && vodafone is null && bankName is null && holder is null && iban is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>()
            {
                ["payout"] = ["Add at least one payout method so the academy can pay you."]
            });

        var teacherId = ApiEndpoints.UserId(principal);
        var details = await db.TeacherPayoutDetails
            .FirstOrDefaultAsync(x => x.TeacherId == teacherId);
        if (details is null)
        {
            details = new TeacherPayoutDetail { TeacherId = teacherId };
            db.TeacherPayoutDetails.Add(details);
        }

        details.InstaPayAccount = instapay ?? "";
        details.VodafoneCashNumber = vodafone ?? "";
        details.BankName = bankName ?? "";
        details.BankAccountHolder = holder ?? "";
        details.BankIban = iban ?? "";
        details.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await realtime.ToUserAsync(teacherId, RealtimeScopes.Billing);
        await realtime.ToStaffAsync(RealtimeScopes.Billing);

        return Results.Ok(ToDetails(teacherId, details));
    }

    private static async Task<IResult> AdminOverviewAsync(
        string? month,
        AcademyDbContext db,
        UserManager<AcademyUser> users)
    {
        var teachers = await users.GetUsersInRoleAsync("teacher");
        var rates = await db.TeacherBillingRates.AsNoTracking().ToDictionaryAsync(x => x.TeacherId);
        var details = await db.TeacherPayoutDetails.AsNoTracking().ToDictionaryAsync(x => x.TeacherId);

        var windowStart = MonthBounds(CurrentMonth().AddMonths(-11)).Start;
        var recent = await db.Lessons
            .AsNoTracking()
            .Where(x => x.Status == "completed" && x.StartsAt >= windowStart)
            .Select(x => new { x.TeacherId, x.StartsAt })
            .ToListAsync();
        var months = BuildMonths(recent.Select(x =>
            (x.StartsAt, rates.GetValueOrDefault(x.TeacherId)?.PayoutEgp ?? 0m)));

        var resolved = ResolveMonth(month, months);
        if (resolved is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["month"] = ["Choose a valid month (YYYY-MM)."] });
        var (selected, key) = resolved.Value;

        var (start, end) = MonthBounds(selected);
        var lessons = await db.Lessons
            .AsNoTracking()
            .Where(x => x.Status == "completed" && x.StartsAt >= start && x.StartsAt < end)
            .Select(x => new { x.TeacherId })
            .ToListAsync();

        var payouts = teachers
            .OrderBy(x => x.FullName)
            .Select(teacher =>
            {
                var count = lessons.Count(x => x.TeacherId == teacher.Id);
                var perLesson = rates.GetValueOrDefault(teacher.Id)?.PayoutEgp ?? 0m;
                return new AdminTeacherPayoutDto(
                    teacher.Id,
                    teacher.FullName,
                    teacher.Email ?? "",
                    count,
                    perLesson,
                    count * perLesson,
                    ToDetails(teacher.Id, details.GetValueOrDefault(teacher.Id)));
            })
            .ToList();

        var rateRows = teachers
            .OrderBy(x => x.FullName)
            .Select(teacher =>
            {
                var rate = rates.GetValueOrDefault(teacher.Id);
                return new TeacherBillingRateDto(
                    teacher.Id,
                    teacher.FullName,
                    teacher.Email ?? "",
                    rate?.StudentPriceUsd ?? 0m,
                    rate?.PayoutEgp ?? 0m);
            })
            .ToList();

        var claims = await db.MonthlyPaymentClaims
            .AsNoTracking()
            .Include(x => x.Student)
            .OrderByDescending(x => x.ClaimedAt)
            .Take(100)
            .ToListAsync();

        var bank = await LoadBankAsync(db);
        return Results.Ok(new AdminBillingOverviewDto(
            bank is null ? null : ToBank(bank),
            rateRows,
            claims.Select(x => ToClaim(x, x.Student)).ToList(),
            payouts,
            key));
    }

    private static async Task<IResult> SaveAcademyAccountAsync(
        UpdateAcademyBankAccountRequest input,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        RealtimeNotifier realtime)
    {
        var bankName = Clean(input.BankName, 120);
        var holder = Clean(input.AccountHolder, 160);
        if (bankName is null || holder is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>()
            {
                ["account"] = ["Enter at least the bank name and the account holder."]
            });

        var account = await db.AcademyBankAccounts.FirstOrDefaultAsync(x => x.Id == BankAccountId);
        if (account is null)
        {
            account = new AcademyBankAccount { Id = BankAccountId };
            db.AcademyBankAccounts.Add(account);
        }

        account.BankName = bankName;
        account.AccountHolder = holder;
        account.AccountNumber = Clean(input.AccountNumber, 64) ?? "";
        account.Iban = Clean(input.Iban, 64) ?? "";
        account.Swift = Clean(input.Swift, 32) ?? "";
        account.Notes = Clean(input.Notes, 500) ?? "";
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.UpdatedBy = ApiEndpoints.UserId(principal);
        await db.SaveChangesAsync();
        await realtime.ToAllAsync(RealtimeScopes.Billing);

        return Results.Ok(ToBank(account));
    }

    private static async Task<IResult> SaveRateAsync(
        Guid teacherId,
        UpdateTeacherBillingRateRequest input,
        AcademyDbContext db,
        UserManager<AcademyUser> users,
        RealtimeNotifier realtime)
    {
        if (input.StudentPriceUsd < 0 || input.PayoutEgp < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>() { ["rate"] = ["Rates cannot be negative."] });

        var teacher = await users.FindByIdAsync(teacherId.ToString());
        if (teacher is null || !await users.IsInRoleAsync(teacher, "teacher"))
            return Results.NotFound();

        var rate = await db.TeacherBillingRates.FirstOrDefaultAsync(x => x.TeacherId == teacherId);
        if (rate is null)
        {
            rate = new TeacherBillingRate { TeacherId = teacherId };
            db.TeacherBillingRates.Add(rate);
        }

        rate.StudentPriceUsd = decimal.Round(input.StudentPriceUsd, 2);
        rate.PayoutEgp = decimal.Round(input.PayoutEgp, 2);
        rate.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await realtime.ToUserAsync(teacherId, RealtimeScopes.Billing);
        await realtime.ToStaffAsync(RealtimeScopes.Billing);

        return Results.Ok(new TeacherBillingRateDto(
            teacher.Id, teacher.FullName, teacher.Email ?? "",
            rate.StudentPriceUsd, rate.PayoutEgp));
    }

    private static async Task<IResult> ConfirmClaimAsync(
        Guid id,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        RealtimeNotifier realtime,
        AcademyPushSender push)
    {
        var claim = await db.MonthlyPaymentClaims
            .Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (claim is null) return Results.NotFound();
        if (claim.Status != "pending")
            return Results.Conflict(new { message = "This claim has already been reviewed." });

        claim.Status = "confirmed";
        claim.ReviewedAt = DateTimeOffset.UtcNow;
        claim.ReviewedBy = ApiEndpoints.UserId(principal);
        claim.AdminNote = null;
        var confirmTitle = "Payment confirmed";
        var confirmBody = $"The academy confirmed your payment for {claim.Month} (${claim.AmountUsd:0.00}).";
        db.Notifications.Add(new AppNotification
        {
            UserId = claim.StudentId,
            Title = confirmTitle,
            Body = confirmBody,
            Kind = "payment",
            Link = "/payments",
            DedupeKey = $"payment-confirmed:{claim.Id}",
        });
        await db.SaveChangesAsync();
        await realtime.ToUserAsync(claim.StudentId, RealtimeScopes.Billing);
        await realtime.ToUserAsync(claim.StudentId, RealtimeScopes.Notifications);
        await realtime.ToStaffAsync(RealtimeScopes.Billing);
        await LessonOpsMessaging.PushAsync(db, push, [claim.StudentId], confirmTitle, confirmBody, "/payments");

        return Results.Ok(ToClaim(claim, claim.Student));
    }

    private static async Task<IResult> RejectClaimAsync(
        Guid id,
        ReviewPaymentClaimRequest input,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        RealtimeNotifier realtime,
        AcademyPushSender push)
    {
        var claim = await db.MonthlyPaymentClaims
            .Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (claim is null) return Results.NotFound();
        if (claim.Status != "pending")
            return Results.Conflict(new { message = "This claim has already been reviewed." });

        claim.Status = "rejected";
        claim.ReviewedAt = DateTimeOffset.UtcNow;
        claim.ReviewedBy = ApiEndpoints.UserId(principal);
        claim.AdminNote = Clean(input.Note, 500);
        var rejectTitle = "Payment needs attention";
        var rejectBody = string.IsNullOrWhiteSpace(claim.AdminNote)
            ? $"The academy could not confirm your payment for {claim.Month}. Open payments for details."
            : $"The academy could not confirm your {claim.Month} payment: {claim.AdminNote}";
        db.Notifications.Add(new AppNotification
        {
            UserId = claim.StudentId,
            Title = rejectTitle,
            Body = rejectBody,
            Kind = "payment",
            Link = "/payments",
            DedupeKey = $"payment-rejected:{claim.Id}:{claim.ReviewedAt.Value.UtcTicks}",
        });
        await db.SaveChangesAsync();
        await realtime.ToUserAsync(claim.StudentId, RealtimeScopes.Billing);
        await realtime.ToUserAsync(claim.StudentId, RealtimeScopes.Notifications);
        await realtime.ToStaffAsync(RealtimeScopes.Billing);
        await LessonOpsMessaging.PushAsync(db, push, [claim.StudentId], rejectTitle, rejectBody, "/payments");

        return Results.Ok(ToClaim(claim, claim.Student));
    }

    private static async Task<Dictionary<Guid, decimal>> LoadRatesAsync(
        AcademyDbContext db,
        IEnumerable<Guid> teacherIds)
    {
        var ids = teacherIds.Distinct().ToList();
        return await db.TeacherBillingRates
            .AsNoTracking()
            .Where(x => ids.Contains(x.TeacherId))
            .ToDictionaryAsync(x => x.TeacherId, x => x.StudentPriceUsd);
    }

    private static decimal StudentAmount(Lesson lesson, Dictionary<Guid, decimal> rates) =>
        lesson.IsTrial ? 0m : rates.GetValueOrDefault(lesson.TeacherId);

    private static Task<AcademyBankAccount?> LoadBankAsync(AcademyDbContext db) =>
        db.AcademyBankAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == BankAccountId);

    private static List<BillingMonthDto> BuildMonths(
        IEnumerable<(DateTimeOffset StartsAt, decimal Amount)> lessons)
    {
        var months = lessons
            .GroupBy(x => MonthKey(x.StartsAt))
            .Select(group => new BillingMonthDto(group.Key, group.Count(), group.Sum(x => x.Amount)))
            .OrderByDescending(x => x.Month, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        var current = MonthKey(DateTimeOffset.UtcNow);
        if (months.All(x => x.Month != current))
            months.Insert(0, new BillingMonthDto(current, 0, 0m));
        return months;
    }

    private static (DateOnly Month, string Key)? ResolveMonth(
        string? input,
        IReadOnlyList<BillingMonthDto> months)
    {
        if (!string.IsNullOrWhiteSpace(input))
            return TryParseMonth(input, out var parsed) ? (parsed, MonthKey(parsed)) : null;

        var fallback = months.FirstOrDefault()?.Month;
        if (!TryParseMonth(fallback, out var month)) return null;
        return (month, MonthKey(month));
    }

    private static bool TryParseMonth(string? value, out DateOnly month)
    {
        month = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParseExact(
                value,
                MonthFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out month);
    }

    private static string MonthKey(DateOnly month) =>
        month.ToString(MonthFormat, CultureInfo.InvariantCulture);

    private static string MonthKey(DateTimeOffset value) =>
        MonthKey(DateOnly.FromDateTime(ToAcademy(value)));

    private static DateOnly CurrentMonth() =>
        DateOnly.FromDateTime(ToAcademy(DateTimeOffset.UtcNow));

    private static DateTime ToAcademy(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, AcademyTimeZone).DateTime;

    private static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(DateOnly month)
    {
        var start = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (ToOffset(start), ToOffset(start.AddMonths(1)));
    }

    // Npgsql only allows UTC-offset values as timestamptz parameters.
    private static DateTimeOffset ToOffset(DateTime local) =>
        new DateTimeOffset(local, AcademyTimeZone.GetUtcOffset(local)).ToUniversalTime();

    private static TimeZoneInfo ResolveAcademyTimeZone()
    {
        foreach (var id in new[] { "Africa/Cairo", "Egypt Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    private static AcademyBankAccountDto ToBank(AcademyBankAccount account) =>
        new(
            account.Id,
            account.BankName,
            account.AccountHolder,
            account.AccountNumber,
            account.Iban,
            account.Swift,
            account.Notes,
            account.UpdatedAt);

    private static TeacherPayoutDetailDto ToDetails(Guid teacherId, TeacherPayoutDetail? details) =>
        new(
            teacherId,
            details?.InstaPayAccount ?? "",
            details?.VodafoneCashNumber ?? "",
            details?.BankName ?? "",
            details?.BankAccountHolder ?? "",
            details?.BankIban ?? "",
            details?.UpdatedAt ?? default);

    private static bool HasDetails(TeacherPayoutDetail? details) =>
        details is not null
        && (!string.IsNullOrWhiteSpace(details.InstaPayAccount)
            || !string.IsNullOrWhiteSpace(details.VodafoneCashNumber)
            || !string.IsNullOrWhiteSpace(details.BankIban));

    private static MonthlyPaymentClaimDto ToClaim(
        MonthlyPaymentClaim claim,
        AcademyUser? student) =>
        new(
            claim.Id,
            claim.StudentId,
            student?.FullName ?? "",
            student?.Email ?? "",
            claim.Month,
            claim.LessonCount,
            claim.AmountUsd,
            claim.Status,
            claim.StudentNote,
            claim.AdminNote,
            claim.ClaimedAt,
            claim.ReviewedAt);

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
