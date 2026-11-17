using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Academy.Api;

/// <summary>
/// The staged path from an approved application to a teaching teacher: the
/// admin proposes intro-meeting times, the applicant picks one through a public
/// single-use link and joins the room without an account, and only when an
/// admin finishes onboarding after the meeting is the teacher account created.
/// </summary>
public static class TeacherOnboardingEndpoints
{
    private static readonly TimeSpan MeetingLength = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    public static void MapTeacherOnboarding(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        var admin = api.MapGroup("/admin").RequireAuthorization("AdminOnly");
        admin.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        admin.MapPost("/teacher-applications/{id:guid}/meeting-slots", ProposeSlotsAsync);
        admin.MapPost("/teacher-applications/{id:guid}/finish-onboarding", FinishOnboardingAsync);

        // The picker is public by design: the applicant has no account until
        // onboarding is finished. The token is 256 random bits, hashed at rest,
        // single-use.
        api.MapGet("/onboarding/{token}", GetInviteAsync)
            .AllowAnonymous().RequireRateLimiting("otp");
        api.MapPost("/onboarding/{token}/pick", PickSlotAsync)
            .AllowAnonymous().RequireRateLimiting("otp");

        api.MapGet("/onboarding/meeting", GetMeetingAsync).RequireAuthorization();
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // The link must open on the frontend the admin is actually using (tunnel in
    // dev, apex in prod). A configured base wins; otherwise the browser's own
    // Origin on the proxied POST is the frontend origin.
    private static string PublicBaseUrl(HttpRequest request, IConfiguration config)
    {
        var configured = (config["Frontend:BaseUrl"] ?? "").Trim().TrimEnd('/');
        if (configured.Length > 0) return configured;
        var origin = request.Headers.Origin.ToString();
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return $"{uri.Scheme}://{uri.Authority}";
        return $"{request.Scheme}://{request.Host}";
    }

    // The applicant joins the meeting straight from the emailed link, before
    // any account exists, so the email must carry the video room itself.
    private static string JitsiDomain(IConfiguration config)
    {
        var domain = (config["Jitsi:Domain"] ?? "").Trim();
        if (domain.Length == 0) domain = "meet.jit.si";
        return domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');
    }

    private static void Audit(
        AcademyDbContext db, Guid? actorId, HttpContext http, string action, Guid applicationId, string detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = action,
            EntityType = "TeacherApplication",
            EntityId = applicationId,
            Detail = detail,
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
        });

    private static async Task<TeacherApplication?> FindByTokenAsync(
        AcademyDbContext db, string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        var application = await db.TeacherApplications
            .FirstOrDefaultAsync(x => x.OnboardingTokenHash == hash, ct);
        if (application is null ||
            application.OnboardingTokenExpiresAt is null ||
            application.OnboardingTokenExpiresAt < DateTimeOffset.UtcNow)
            return null;
        return application;
    }

    private static object InvitePayload(TeacherApplication application) => new
    {
        fullName = application.FullName,
        slots = TeacherApplicationEndpoints.ParseSlots(application.MeetingSlotsJson),
        meetingStartsAt = application.MeetingStartsAt,
        meetingEndsAt = application.MeetingEndsAt,
        picked = application.MeetingStartsAt is not null,
        expiresAt = application.OnboardingTokenExpiresAt,
    };

    private static async Task<IResult> GetInviteAsync(
        string token,
        [FromServices] AcademyDbContext db,
        HttpContext http)
    {
        var application = await FindByTokenAsync(db, token, http.RequestAborted);
        if (application is null)
            return Results.NotFound(new { message = "This link is invalid or has expired. Ask the academy to send a new one." });
        return Results.Ok(InvitePayload(application));
    }

    private sealed record ProposeSlotsRequest(List<string>? Slots);

    private static async Task<IResult> ProposeSlotsAsync(
        Guid id,
        ProposeSlotsRequest input,
        ClaimsPrincipal principal,
        HttpContext http,
        [FromServices] AcademyDbContext db,
        [FromServices] AcademyEmailSender email,
        [FromServices] IConfiguration config)
    {
        var application = await db.TeacherApplications.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (application is null) return Results.NotFound();
        if (application.Status != "approved")
            return Results.Conflict(new { message = "Only an approved application can be onboarded." });
        if (application.OnboardedAt is not null || application.TeacherId is not null)
            return Results.Conflict(new { message = "This teacher already finished onboarding." });
        if (application.MeetingStartsAt is not null)
            return Results.Conflict(new { message = "A meeting is already scheduled for this teacher." });

        var now = DateTimeOffset.UtcNow;
        var slots = new List<DateTimeOffset>();
        foreach (var raw in input.Slots ?? [])
        {
            if (!DateTimeOffset.TryParse(raw, out var slot) || slot.Offset != TimeSpan.Zero)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slots"] = ["Send each time as an ISO UTC timestamp."] });
            if (slot <= now.AddMinutes(5))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slots"] = ["Offer times at least a few minutes in the future."] });
            if (!slots.Contains(slot)) slots.Add(slot);
        }
        if (slots.Count is < 1 or > 3)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["slots"] = ["Offer between one and three times."] });
        slots.Sort();

        var token = NewToken();
        application.MeetingSlotsJson = JsonSerializer.Serialize(slots);
        application.OnboardingTokenHash = HashToken(token);
        application.OnboardingTokenExpiresAt = now.Add(TokenLifetime);
        Audit(db, ApiEndpoints.UserId(principal), http, "teacher_onboarding_slots_proposed", application.Id,
            $"{slots.Count} slot(s) offered; picker link emailed.");
        await db.SaveChangesAsync(http.RequestAborted);

        var link = $"{PublicBaseUrl(http.Request, config)}/onboarding/{token}";
        var emailed = await email.SendNoticeAsync(
            application.Email,
            "Pick your Ana Quran intro meeting time",
            $"Hello {application.FullName},\n\nYour teacher application is approved. Choose a time for a short intro meeting with the academy team:\n{link}\n\n"
                + "The link expires in 7 days and can be used once. Times are shown in your local timezone.\n\n"
                + "If you were not expecting this email, you can ignore it.",
            http.RequestAborted);

        return Results.Ok(new
        {
            application = await ToDtoAsync(db, application, http.RequestAborted),
            emailed,
            // Same fallback as the approval invite: without SMTP the admin
            // delivers the link by hand instead of the teacher getting nothing.
            pickerUrl = emailed ? null : link,
        });
    }

    private sealed record PickSlotRequest(string? Slot);

    private static async Task<IResult> PickSlotAsync(
        string token,
        PickSlotRequest input,
        HttpContext http,
        [FromServices] AcademyDbContext db,
        [FromServices] AcademyEmailSender email,
        [FromServices] RealtimeNotifier realtime,
        [FromServices] IConfiguration config)
    {
        var application = await FindByTokenAsync(db, token, http.RequestAborted);
        if (application is null)
            return Results.NotFound(new { message = "This link is invalid or has expired. Ask the academy to send a new one." });
        if (application.MeetingStartsAt is not null)
            return Results.Conflict(new { message = "A meeting time was already picked with this link." });

        var slots = TeacherApplicationEndpoints.ParseSlots(application.MeetingSlotsJson);
        if (!DateTimeOffset.TryParse(input.Slot, out var picked) ||
            picked.Offset != TimeSpan.Zero || !slots.Contains(picked))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["Pick one of the offered times."] });
        if (picked <= DateTimeOffset.UtcNow)
            return Results.Conflict(new { message = "That time has passed. Ask the academy to offer new times." });

        var baseUrl = PublicBaseUrl(http.Request, config);
        application.MeetingStartsAt = picked;
        application.MeetingEndsAt = picked.Add(MeetingLength);
        application.MeetingRoom = $"anaquran-onboarding-{application.Id:N}";
        // Single-use: the picker link dies with the pick.
        application.OnboardingTokenHash = null;
        application.OnboardingTokenExpiresAt = null;

        var adminId = application.ReviewedBy;
        if (adminId is not null)
            db.Notifications.Add(LessonOpsMessaging.Notice(
                adminId.Value, "Intro meeting scheduled",
                $"{application.FullName} picked an intro meeting time. Open the application to join the room.",
                "booking", "/admin/teacher-applications", $"onboarding-meeting:{application.Id}:admin"));
        Audit(db, null, http, "teacher_onboarding_meeting_picked", application.Id,
            $"Meeting at {picked:u} in room {application.MeetingRoom}.");
        await db.SaveChangesAsync(http.RequestAborted);

        // The applicant has no account yet, so their link opens the video room
        // directly; staff join through the signed-in meeting page.
        var joinUrl = $"https://{JitsiDomain(config)}/{application.MeetingRoom}";
        var adminJoinUrl = $"{baseUrl}/onboarding/meeting?applicationId={application.Id}";
        var when = $"{picked.UtcDateTime:dddd, d MMMM yyyy 'at' h:mm tt} UTC ({MeetingLength.TotalMinutes:0} minutes)";
        await email.SendNoticeAsync(
            application.Email,
            "Your Ana Quran intro meeting is booked",
            $"Hello {application.FullName},\n\nYour intro meeting with the academy team is booked for {when}.\n\nJoin here when it starts: {joinUrl}\nNo sign-in is needed — the link opens the meeting room directly.\n\nIf you were not expecting this email, you can ignore it.",
            http.RequestAborted);
        if (adminId is not null)
        {
            var admin = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == adminId, http.RequestAborted);
            if (admin?.Email is { Length: > 0 })
                await email.SendNoticeAsync(
                    admin.Email,
                    $"Intro meeting booked with {application.FullName}",
                    $"{application.FullName} picked {when} for their intro meeting.\n\nJoin here when it starts: {adminJoinUrl}",
                    http.RequestAborted);
            await realtime.ToUsersAsync([adminId.Value], RealtimeScopes.Notifications);
        }

        return Results.Ok(new
        {
            meetingStartsAt = application.MeetingStartsAt,
            meetingEndsAt = application.MeetingEndsAt,
            joinUrl,
        });
    }

    private static async Task<IResult> FinishOnboardingAsync(
        Guid id,
        ClaimsPrincipal principal,
        HttpContext http,
        [FromServices] AcademyDbContext db,
        [FromServices] UserManager<AcademyUser> users,
        [FromServices] PasswordResetStore resets,
        [FromServices] AcademyEmailSender email,
        [FromServices] RealtimeNotifier realtime)
    {
        var application = await db.TeacherApplications.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (application is null) return Results.NotFound();
        if (application.Status != "approved")
            return Results.Conflict(new { message = "Only an approved application can be onboarded." });
        if (application.OnboardedAt is not null || application.TeacherId is not null)
            return Results.Conflict(new { message = "This teacher already finished onboarding." });
        if (application.MeetingStartsAt is null)
            return Results.Conflict(new { message = "Schedule the intro meeting before finishing onboarding." });

        // The account is born here, after the meeting — not at approval.
        string? inviteCode;
        Guid teacherId;
        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);

        if (await users.FindByEmailAsync(application.Email) is not null)
            return Results.Conflict(new { message = "An account with this email already exists. Review or link it manually." });

        var teacher = new AcademyUser
        {
            Id = Guid.NewGuid(),
            UserName = application.Email,
            Email = application.Email,
            FullName = application.FullName,
            PhoneNumber = application.PhoneNumber,
            PhoneNumberConfirmed = true,
            EmailConfirmed = true,
        };
        var created = await users.CreateAsync(teacher, TeacherApplicationEndpoints.GenerateTemporaryPassword());
        if (!created.Succeeded)
            return Results.ValidationProblem(ApiEndpoints.IdentityErrors(created));

        var roleAdded = await users.AddToRoleAsync(teacher, "teacher");
        if (!roleAdded.Succeeded)
        {
            await users.DeleteAsync(teacher);
            return Results.Problem("Could not assign the teacher role. Please try again.", statusCode: StatusCodes.Status500InternalServerError);
        }

        teacherId = teacher.Id;
        application.TeacherId = teacherId;
        application.OnboardedAt = DateTimeOffset.UtcNow;
        inviteCode = resets.TryIssue(application.Email.ToLowerInvariant());
        db.Notifications.Add(LessonOpsMessaging.Notice(
            teacherId, "Onboarding complete",
            "Welcome to Ana Quran Academy. Sign the academy agreement from your dashboard, then the academy can assign students to you.",
            "booking", "/teacher/dashboard", $"teacher-onboarded:{application.Id}"));
        Audit(db, ApiEndpoints.UserId(principal), http, "teacher_onboarding_finished", application.Id,
            $"Teacher account {teacherId} created after the intro meeting.");
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
            await transaction.CommitAsync(http.RequestAborted);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(http.RequestAborted);
            return Results.Conflict(new { message = "An account with this email already exists. Review or link it manually." });
        }

        // Email delivery happens after the commit so a slow SMTP server never
        // holds the database transaction open.
        var inviteEmailed = inviteCode is not null &&
            await email.SendTeacherInviteCodeAsync(application.Email, application.FullName, inviteCode, 15, http.RequestAborted);
        await realtime.ToUserAsync(teacherId, RealtimeScopes.Notifications);
        await realtime.ToStaffAsync(RealtimeScopes.Directory);

        return Results.Ok(new
        {
            application = await ToDtoAsync(db, application, http.RequestAborted),
            inviteEmailed,
            inviteCode = inviteEmailed ? null : inviteCode,
        });
    }

    private static async Task<IResult> GetMeetingAsync(
        ClaimsPrincipal principal,
        HttpContext http,
        [FromServices] AcademyDbContext db,
        Guid? applicationId)
    {
        var userId = ApiEndpoints.UserId(principal);
        var isAdmin = principal.IsInRole("admin") || principal.IsInRole("super_admin");
        TeacherApplication? application;
        if (isAdmin)
        {
            if (applicationId is null)
                return Results.BadRequest(new { message = "applicationId is required for staff." });
            application = await db.TeacherApplications.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == applicationId, http.RequestAborted);
        }
        else if (principal.IsInRole("teacher"))
        {
            application = await db.TeacherApplications.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TeacherId == userId && x.Status == "approved", http.RequestAborted);
        }
        else return Results.Forbid();

        if (application?.MeetingStartsAt is null || application.MeetingRoom is null)
            return Results.NotFound(new { message = "No meeting is scheduled yet." });

        return Results.Ok(new
        {
            applicationId = application.Id,
            room = application.MeetingRoom,
            startsAt = application.MeetingStartsAt,
            endsAt = application.MeetingEndsAt,
            counterpartName = isAdmin ? application.FullName : "Ana Quran Academy team",
            onboardedAt = application.OnboardedAt,
        });
    }

    private static async Task<TeacherApplicationDto> ToDtoAsync(
        AcademyDbContext db, TeacherApplication application, CancellationToken ct)
    {
        var files = await db.TeacherApplications.AsNoTracking()
            .Where(x => x.Id == application.Id)
            .Select(x => new
            {
                CvFileName = x.Cv == null ? null : x.Cv.FileName,
                IntroFileName = x.Intro == null ? null : x.Intro.FileName,
                QuranRecordingFileName = x.QuranRecording == null ? null : x.QuranRecording.FileName,
            })
            .FirstAsync(ct);
        return new TeacherApplicationDto(
            application.Id, application.FullName, application.Email, application.PhoneNumber,
            application.Subjects.Split(',', StringSplitOptions.RemoveEmptyEntries),
            application.EnglishLevel, application.ExperienceYears, application.Bio, application.AvailabilityNote,
            application.Status, application.RejectionReason, application.CreatedAt,
            application.ReviewedAt, application.TeacherId, files.CvFileName, files.IntroFileName,
            files.QuranRecordingFileName, TeacherApplicationEndpoints.ParseSlots(application.MeetingSlotsJson),
            application.MeetingStartsAt, application.MeetingEndsAt, application.OnboardedAt);
    }
}