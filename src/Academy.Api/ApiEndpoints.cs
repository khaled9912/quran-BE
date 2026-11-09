using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Data;
using System.Globalization;
using Npgsql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public static class ApiEndpoints
{
    internal static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static bool HasRole(ClaimsPrincipal p, string role) => p.IsInRole(role);
    private static bool IsAdmin(ClaimsPrincipal p) => p.IsInRole("admin") || p.IsInRole("super_admin");
    private static void Audit(AcademyDbContext db, Guid? actorId, string action, string entityType, Guid? entityId, string detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail.Length > 2000 ? detail[..2000] : detail,
        });
    // The school-grade dropdown values shared by the web and mobile forms.
    public static readonly string[] StudentGrades = new[]
    {
        "Kindergarten", "Grade 1", "Grade 2", "Grade 3", "Grade 4", "Grade 5", "Grade 6",
        "Grade 7", "Grade 8", "Grade 9", "Grade 10", "Grade 11", "Grade 12",
    };

    private static UserDto ToUser(AcademyUser u, string role)
    {
        var name = DisplayName(u.FullName);
        return new(u.Id, name, u.Email ?? "", role.ToLowerInvariant(), u.IsActive, name, u.IsActive, name, u.PhoneNumber, $"/api/users/{u.Id}/avatar", u.Grade);
    }

    // What the other side of a lesson is allowed to see: who they are, never how
    // to reach them. Contact details stay inside staff and self-service responses.
    private static UserDto ToPerson(AcademyUser u, string role)
    {
        var person = ToUser(u, role) with { Email = "", PhoneNumber = null };
        if (person.FullName is not null) return person;
        // This caller has no address to fall back to, so a missing name leaves only
        // the role to label the person with.
        var label = role == "teacher" ? "Teacher" : "Student";
        return person with { FullName = label, FullNameSnake = label, Name = label };
    }

    // A name field is for a person, so an email address in it is a mistake even
    // when the address is valid, and it would leak into every listing.
    internal static bool NameContainsContact(string? fullName) => fullName is not null && fullName.Contains('@');

    /// <summary>
    /// The guard above only covers writes, so accounts created before it can still
    /// hold an address in the name column. Reads report no name for those rows:
    /// null means "unknown" and lets the fallback a caller already has — a client's
    /// `?? email`, a sentence's `?? "your student"` — take over rather than print
    /// an address where a person's name belongs.
    /// </summary>
    internal static string? DisplayName(string? fullName) => NameContainsContact(fullName) ? null : fullName;
    private static async Task<string> RoleOf(UserManager<AcademyUser> users, AcademyUser user)
        => (await users.GetRolesAsync(user)).FirstOrDefault() ?? "student";
    private static LessonRequestDto ToRequest(LessonRequest r)
    {
        var startsAt = r.AvailabilitySlot?.StartsAt ?? r.StudentAvailability!.StartsAt;
        var endsAt = r.AvailabilitySlot?.EndsAt ?? r.StudentAvailability!.EndsAt;
        return new(r.Id, r.TeacherId, DisplayName(r.Teacher?.FullName) ?? "Teacher", r.StudentId, DisplayName(r.Student?.FullName) ?? "Student", r.AvailabilitySlotId, r.StudentAvailabilityId, r.AvailabilitySlotId.HasValue ? "student" : "teacher", startsAt, endsAt, r.Status, r.CreatedAt, r.RejectionReason);
    }
    /// <summary>
    /// Identity reports a duplicate sign-in name and a duplicate email as two
    /// separate errors. Here both are the same value, so they would surface twice;
    /// collapse them into one message.
    /// </summary>
    internal static Dictionary<string, string[]> IdentityErrors(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            return new() { ["email"] = ["An account with this email already exists."] };
        return result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray());
    }
    /// <summary>
    /// The sign-in answer is read by whoever typed the credentials, so it is
    /// product copy. Both apps display ProblemDetails `detail` and treat `title`
    /// as infrastructure wording, which is why the message goes in `detail`.
    /// </summary>
    internal static IResult SignInFailure() =>
        Results.Problem(title: "Sign in failed", detail: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Identity remembers the moment a locked account opens again, so the wait a
    /// caller is told about is the one actually remaining, not the full window.
    /// </summary>
    internal static IResult SignInLockedOut(HttpResponse response, AcademyUser user)
    {
        var remaining = user.LockoutEnd is { } until ? until - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
        // Whole minutes, rounded up: "about 1 minute" must not expire early.
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Results.Problem(
            title: "Sign in locked",
            detail: $"Too many failed attempts. Sign in is locked for about {minutes} minute{(minutes == 1 ? "" : "s")}.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static string PublicStudentName(string? fullName)
    {
        // Splitting on spaces alone would hand back an address whole, since it has
        // no space in it.
        if (NameContainsContact(fullName)) return "Student";
        var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "Student",
            1 => parts[0],
            _ => $"{parts[0]} {parts[^1][0]}."
        };
    }
    private static LessonReportDto ToReport(LessonReport r, Lesson l) => new(r.Id, l.Id, l.StartsAt, l.EndsAt, l.TeacherId, DisplayName(l.Teacher?.FullName) ?? "Teacher", l.StudentId, DisplayName(l.Student?.FullName) ?? "Student", r.ClassSummary, r.StudentFeedback, r.Progress, r.CreatedAt, r.UpdatedAt);
    private static TeacherReviewDto ToReview(TeacherReview r, Lesson l) => new(r.Id, l.Id, l.StartsAt, r.TeacherId, DisplayName(r.Teacher?.FullName ?? l.Teacher?.FullName) ?? "Teacher", r.StudentId, PublicStudentName(r.Student?.FullName ?? l.Student?.FullName), r.Rating, r.Comment, r.CreatedAt, r.UpdatedAt);
    private static TeacherReviewDto ToReview(TeacherReview r) => r.Lesson is null
        ? new(r.Id, r.LessonId, default, r.TeacherId, DisplayName(r.Teacher?.FullName) ?? "Teacher", r.StudentId, PublicStudentName(r.Student?.FullName), r.Rating, r.Comment, r.CreatedAt, r.UpdatedAt)
        : ToReview(r, r.Lesson);
    internal static LessonDto ToLesson(Lesson l) => new(l.Id, l.TeacherId, DisplayName(l.Teacher?.FullName) ?? "Teacher", l.StudentId, DisplayName(l.Student?.FullName) ?? "Student", l.StartsAt, l.EndsAt, l.Status, l.ActualStartedAt, l.ActualEndedAt, l.Report is null ? null : ToReport(l.Report, l), l.Review is null ? null : ToReview(l.Review, l), l.SeriesId, l.IsTrial, l.CancellationOutcome, l.CancelReason, l.CancelledAt, l.CancelledById, l.IsMakeup, l.OriginalLessonId);
    private static async Task<(double? Average, int Count)> TeacherRatingAsync(AcademyDbContext db, Guid teacherId)
    {
        var count = await db.TeacherReviews.CountAsync(x => x.TeacherId == teacherId);
        var average = count == 0 ? (double?)null : await db.TeacherReviews.Where(x => x.TeacherId == teacherId).AverageAsync(x => (double)x.Rating);
        return (average, count);
    }

    public static void MapAcademyApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        var auth = api.MapGroup("/auth").AllowAnonymous();
        auth.MapPost("/register", async (RegisterRequest input, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["registration"] = ["Full name, valid email and a password of at least 8 characters are required."] });
            if (NameContainsContact(input.FullName))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fullName"] = ["Enter the student's name, not an email address."] });
            if (!RegistrationPolicy.IsAllowed(http, input.Country))
                return Results.Problem(RegistrationPolicy.BlockedMessage, statusCode: 403);
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), EmailConfirmed = true };
            var result = await users.CreateAsync(user, input.Password);
            if (!result.Succeeded) return Results.ValidationProblem(IdentityErrors(result));
            await users.AddToRoleAsync(user, "student");
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Created("/api/auth/me", new { user = ToUser(user, "student") });
        }).RequireRateLimiting("signup");
        auth.MapPost("/login", async (LoginRequest input, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password)) return SignInFailure();
            var user = await users.FindByEmailAsync(input.Email.Trim());
            if (user is null || !user.IsActive) return SignInFailure();
            // lockoutOnFailure records the miss and, at the fifth one, locks the
            // account for the configured window. The result says which happened, so
            // a locked account must not keep answering "invalid credentials".
            var check = await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true);
            if (check.IsLockedOut) return SignInLockedOut(http.Response, user);
            if (!check.Succeeded) return SignInFailure();
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        }).RequireRateLimiting("login");
        auth.MapGet("/me", async (ClaimsPrincipal principal, UserManager<AcademyUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null || !user.IsActive ? Results.Unauthorized() : Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        }).RequireAuthorization();
        // Allow anonymous so a stale/expired session cookie can still be cleared.
        auth.MapPost("/logout", async (SignInManager<AcademyUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });

        // Self-service password change. Mapped on the root group so the auth
        // group's AllowAnonymous flag cannot bypass authorization.
        api.MapPost("/auth/change-password", async (ChangePasswordRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn, AcademyDbContext db) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!await users.HasPasswordAsync(user))
                return Results.Problem("This account signs in with a phone code, so there is no password to change.", statusCode: 409);
            if (string.IsNullOrWhiteSpace(input.CurrentPassword) || string.IsNullOrWhiteSpace(input.NewPassword))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Enter your current and new password."] });
            if (input.NewPassword.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["The new password must be at least 8 characters."] });
            if (input.CurrentPassword == input.NewPassword)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["Choose a password different from your current one."] });
            var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
            if (!result.Succeeded)
            {
                if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Your current password is incorrect."] });
                return Results.ValidationProblem(IdentityErrors(result));
            }
            // Rotate the cookie for this device; other devices are signed out by the
            // security stamp change inside ChangePasswordAsync.
            Audit(db, user.Id, "password_changed", "User", user.Id, "self-service");
            await db.SaveChangesAsync();
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");

        auth.MapPost("/password/request", async (PasswordResetRequestInput input, PasswordResetStore resets, AcademyEmailSender email, UserManager<AcademyUser> users, HttpContext http, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) =>
        {
            var address = input.Email?.Trim() ?? "";
            if (!System.Net.Mail.MailAddress.TryCreate(address, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });
            if (!resets.DevMode && !email.IsConfigured)
                return Results.Problem("Password reset email is not available right now. Please contact the academy.", statusCode: 503);

            var key = address.ToLowerInvariant();
            var user = await users.FindByEmailAsync(address);
            // The response must not reveal whether the account exists, whether a
            // code was recently issued (cooldown), or whether delivery succeeded.
            // Every outcome below returns the same 202; failures are logged server-side.
            string? issued = null;
            if (user is not null && user.IsActive)
            {
                issued = resets.TryIssue(key);
                if (issued is not null && !resets.DevMode)
                {
                    var sent = await email.SendPasswordResetCodeAsync(user.Email!, issued, 15, http.RequestAborted);
                    if (!sent)
                    {
                        resets.Consume(key);
                        loggerFactory.CreateLogger("PasswordReset").LogError("Password reset email delivery failed for a registered account.");
                    }
                }
            }
            return Results.Accepted(value: new
            {
                message = "If an account uses this email, a reset code has been sent.",
                devCode = resets.DevMode ? issued : null,
            });
        }).RequireRateLimiting("otp");

        auth.MapPost("/password/reset", async (PasswordResetInput input, PasswordResetStore resets, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var address = input.Email?.Trim() ?? "";
            var code = input.Code?.Trim() ?? "";
            if (!System.Net.Mail.MailAddress.TryCreate(address, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });
            if (code.Length is < 4 or > 8 || !code.All(char.IsAsciiDigit))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Enter the numeric code from the email."] });
            if (string.IsNullOrWhiteSpace(input.NewPassword) || input.NewPassword.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["The new password must be at least 8 characters."] });

            var key = address.ToLowerInvariant();
            if (!resets.Check(key, code)) return Results.Problem("The code is invalid or has expired.", statusCode: 401);

            var user = await users.FindByEmailAsync(address);
            if (user is null || !user.IsActive) return Results.Problem("The code is invalid or has expired.", statusCode: 401);

            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(user, token, input.NewPassword);
            if (!result.Succeeded) return Results.ValidationProblem(IdentityErrors(result));
            // A victim who resets a stolen account's password must also lose the
            // attacker's existing session.
            await users.UpdateSecurityStampAsync(user);
            await users.UpdateAsync(user);
            resets.Consume(key);
            Audit(db, user.Id, "password_reset_completed", "User", user.Id, "email-code");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("otp");

        var profile = api.MapGroup("/profile").RequireAuthorization();
        profile.MapGet("", async (ClaimsPrincipal principal, UserManager<AcademyUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null || !user.IsActive
                ? Results.Unauthorized()
                : Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        });

        profile.MapPatch("", async (UpdateProfileRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users, PhoneOtpStore otp, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            var errors = new Dictionary<string, string[]>();
            var fullName = input.FullName?.Trim();
            var email = input.Email?.Trim();
            string? phone = null;

            if (input.FullName is not null && (fullName!.Length is < 2 or > 160))
                errors["fullName"] = ["Enter your full name (2 to 160 characters)."];
            if (input.FullName is not null && NameContainsContact(fullName))
                errors["fullName"] = ["Enter your name, not an email address."];
            if (input.Email is not null && (email!.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(email, out _)))
                errors["email"] = ["Enter a valid email address."];
            if (input.PhoneNumber is not null)
            {
                phone = PhoneNumbers.Normalize(input.PhoneNumber);
                if (phone is null) errors["phoneNumber"] = ["Enter a valid mobile number in international format."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            // Changing a sign-in identifier is a sensitive operation: a stolen
            // session must not be able to rebind the account to an attacker's
            // email/phone and turn transient access into a permanent takeover.
            // Require step-up proof against an existing credential first.
            var emailChanged = email is not null && !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase);
            var phoneChanged = phone is not null && !string.Equals(phone, user.PhoneNumber, StringComparison.Ordinal);
            if (emailChanged || phoneChanged)
            {
                var verified = false;
                if (!string.IsNullOrWhiteSpace(input.CurrentPassword) && await users.HasPasswordAsync(user))
                    verified = await users.CheckPasswordAsync(user, input.CurrentPassword);
                if (!verified && !string.IsNullOrWhiteSpace(input.PhoneCode) && user.PhoneNumberConfirmed && user.PhoneNumber is not null)
                {
                    if (otp.Check(user.PhoneNumber, input.PhoneCode.Trim()))
                    {
                        otp.Consume(user.PhoneNumber);
                        verified = true;
                    }
                }
                if (!verified)
                    return Results.Problem("Confirm your identity with your current password or a code sent to your current mobile number before changing contact details.", statusCode: 403);
            }

            if (emailChanged)
            {
                var emailOwner = await users.FindByEmailAsync(email!);
                if (emailOwner is not null && emailOwner.Id != user.Id)
                    return Results.Conflict(new { message = "An account with this email already exists." });
                var nameOwner = await users.FindByNameAsync(email!);
                if (nameOwner is not null && nameOwner.Id != user.Id)
                    return Results.Conflict(new { message = "That email is already in use." });
                user.Email = email;
                user.UserName = email;
                user.EmailConfirmed = true;
            }
            if (phoneChanged)
            {
                if (await users.Users.AnyAsync(x => x.PhoneNumber == phone && x.Id != user.Id))
                    return Results.Conflict(new { message = "That mobile number is already in use." });
                user.PhoneNumber = phone;
                user.PhoneNumberConfirmed = false;
            }
            if (fullName is not null) user.FullName = fullName;

            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
                return Results.ValidationProblem(updated.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            if (emailChanged || phoneChanged)
                db.AuditLogs.Add(new AuditLog
                {
                    ActorId = user.Id,
                    Action = "profile_identifier_changed",
                    EntityType = "User",
                    EntityId = user.Id,
                    Detail = $"{(emailChanged ? "email " : "")}{(phoneChanged ? "phone" : "")}".Trim(),
                });
            await db.SaveChangesAsync();
            await realtime.ToUserAsync(user.Id, RealtimeScopes.Self);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        });

        profile.MapPost("/avatar", async (HttpContext http, ClaimsPrincipal principal, AcademyDbContext db, UserManager<AcademyUser> users, RealtimeNotifier realtime) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!http.Request.HasFormContentType)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["avatar"] = ["Choose an image to upload."] });

            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(http.RequestAborted);
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            {
                return Results.Problem("The image is too large. Choose an image under 2 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["avatar"] = ["Choose an image to upload."] });
            if (file.Length > MaxAvatarBytes)
                return Results.Problem("The image is too large. Choose an image under 2 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);

            var contentType = (file.ContentType ?? "").Trim().ToLowerInvariant();
            if (contentType == "image/jpg") contentType = "image/jpeg";
            if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["avatar"] = ["Use a PNG, JPEG, or WebP image."] });

            await using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, http.RequestAborted);
            var data = buffer.ToArray();
            if (!LooksLikeImage(data, contentType))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["avatar"] = ["That file is not a valid image."] });

            var avatar = await db.UserAvatars.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (avatar is null)
            {
                avatar = new UserAvatar { UserId = user.Id };
                db.UserAvatars.Add(avatar);
            }
            avatar.ContentType = contentType;
            avatar.Data = data;
            avatar.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await realtime.ToUserAsync(user.Id, RealtimeScopes.Self);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Ok(new { avatarUrl = $"/api/users/{user.Id}/avatar" });
        });

        profile.MapDelete("/avatar", async (ClaimsPrincipal principal, AcademyDbContext db, UserManager<AcademyUser> users, RealtimeNotifier realtime) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var avatar = await db.UserAvatars.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (avatar is not null)
            {
                db.UserAvatars.Remove(avatar);
                await db.SaveChangesAsync();
            }
            await realtime.ToUserAsync(user.Id, RealtimeScopes.Self);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.NoContent();
        });

        api.MapGet("/users/{id:guid}/avatar", async (Guid id, AcademyDbContext db, HttpContext http) =>
        {
            var avatar = await db.UserAvatars.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id);
            if (avatar is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-cache";
            return Results.File(avatar.Data, avatar.ContentType, lastModified: avatar.UpdatedAt);
        }).AllowAnonymous();

        auth.MapPost("/otp/request", async (OtpRequest input, PhoneOtpStore otp, IOtpSender sender, HttpContext http) =>
        {
            var phone = PhoneNumbers.Normalize(input.Phone);
            if (phone is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["phone"] = ["Enter a valid phone number in international format, for example +96170123456."] });
            if (!otp.DevMode && !sender.IsConfigured) return Results.Problem("Code delivery is not configured yet.", statusCode: 503);
            var code = otp.TryIssue(phone);
            if (code is null) return Results.Problem("Please wait a minute before requesting another code.", statusCode: 429);
            if (!otp.DevMode)
            {
                try
                {
                    await sender.SendAsync(phone, code, http.RequestAborted);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Release the cooldown so a delivery failure does not lock the user out.
                    otp.Consume(phone);
                    return Results.Problem("Could not send the code. Please try again.", statusCode: 502);
                }
            }
            return Results.Accepted(value: new
            {
                message = "If this number can receive messages, a code has been sent.",
                devMode = otp.DevMode,
                devCode = otp.DevMode ? otp.DevCode : null,
            });
        }).RequireRateLimiting("otp");

        auth.MapPost("/otp/verify", async (OtpVerifyRequest input, PhoneOtpStore otp, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn, HttpContext http) =>
        {
            var phone = PhoneNumbers.Normalize(input.Phone);
            if (phone is null || string.IsNullOrWhiteSpace(input.Code))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["otp"] = ["Enter the phone number and the code you received."] });
            if (!otp.Check(phone, input.Code.Trim())) return Results.Problem("The code is invalid or has expired.", statusCode: 401);

            var user = users.Users.FirstOrDefault(x => x.PhoneNumber == phone);
            if (user is null)
            {
                // Only new accounts are gated; existing students can sign in from anywhere.
                if (!RegistrationPolicy.IsAllowed(http, input.Country))
                    return Results.Problem(RegistrationPolicy.BlockedMessage, statusCode: 403);
                var fullName = input.FullName?.Trim() ?? "";
                if (fullName.Length == 0) return Results.Ok(new { requiresProfile = true });
                if (fullName.Length is < 2 or > 160)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["fullName"] = ["Enter your full name (2 to 160 characters)."] });
                if (NameContainsContact(fullName))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["fullName"] = ["Enter your name, not an email address."] });
                user = new AcademyUser
                {
                    Id = Guid.NewGuid(),
                    UserName = phone,
                    PhoneNumber = phone,
                    PhoneNumberConfirmed = true,
                    FullName = fullName,
                    EmailConfirmed = true,
                };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded) return Results.ValidationProblem(created.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
                await users.AddToRoleAsync(user, "student");
            }
            if (!user.IsActive) return Results.Problem("This account is deactivated. Contact an administrator.", statusCode: 401);

            if (!user.PhoneNumberConfirmed)
            {
                user.PhoneNumberConfirmed = true;
                await users.UpdateAsync(user);
            }
            otp.Consume(phone);
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        }).RequireRateLimiting("otp");

        api.MapPost("/contact", async (ContactRequest input, AcademyDbContext db, AcademyEmailSender notifier, RealtimeNotifier realtime, HttpContext http) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = input.Name?.Trim() ?? "";
            var email = input.Email?.Trim() ?? "";
            var message = input.Message?.Trim() ?? "";
            if (name.Length is 0 or > 120) errors["name"] = ["Please provide a name of 120 characters or fewer."];
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) errors["email"] = ["Please provide a valid email address."];
            if (message.Length is < 5 or > 3000) errors["message"] = ["Message must be between 5 and 3000 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var submission = new ContactSubmission { Name = name, Email = email, Message = message };
            db.ContactSubmissions.Add(submission);
            await db.SaveChangesAsync();
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            // The receiver is fixed by server configuration, never by the request body.
            await notifier.SendContactMessageAsync(submission, http.RequestAborted);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("contact");

        var admin = api.MapGroup("/admin").RequireAuthorization("AdminOnly");
        admin.MapPost("/users", async (CreateUserRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password) || string.IsNullOrWhiteSpace(input.Role)) return Results.BadRequest(new { message = "Full name, email, password and role are required." });
            var role = input.Role.Trim().ToLowerInvariant();
            if (role is not ("teacher" or "admin" or "support")) return Results.BadRequest(new { message = "Admin can create teacher, support, or admin accounts only." });
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), EmailConfirmed = true };
            var created = await users.CreateAsync(user, input.Password);
            if (!created.Succeeded) return Results.ValidationProblem(IdentityErrors(created));
            await users.AddToRoleAsync(user, role);
            Audit(db, UserId(principal), "admin_user_created", "User", user.Id, $"role={role}");
            await db.SaveChangesAsync();
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/{role}s/{user.Id}", new { user = ToUser(user, role) });
        });
        admin.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var role = await RoleOf(users, user);
            if (role is not ("teacher" or "student" or "support")) return Results.BadRequest(new { message = "Only teacher, student and support accounts can be edited here." });
            var errors = new Dictionary<string, string[]>();
            var name = input.FullName?.Trim();
            var email = input.Email?.Trim();
            if (input.FullName is not null && name!.Length is 0 or > 160) errors["fullName"] = ["Please provide a name of 160 characters or fewer."];
            if (input.Email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _)) errors["email"] = ["Please provide a valid email address."];
            if (input.Password is not null && input.Password.Length < 8) errors["password"] = ["The password must be at least 8 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (email is not null && !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                var existing = await users.FindByEmailAsync(email);
                if (existing is not null && existing.Id != user.Id) return Results.Conflict(new { message = "An account with this email already exists." });
                user.Email = email; user.UserName = email;
            }
            if (name is not null) user.FullName = name;
            if (input.IsActive is not null) user.IsActive = input.IsActive.Value;
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded) return Results.ValidationProblem(updated.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            if (input.Password is not null)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var reset = await users.ResetPasswordAsync(user, token, input.Password);
                if (!reset.Succeeded) return Results.ValidationProblem(reset.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
                // An admin password reset is how a compromised account is taken back,
                // so every session issued before it has to stop working.
                await users.UpdateSecurityStampAsync(user);
                await users.UpdateAsync(user);
            }
            if (input.IsActive == false)
            {
                // Switching the stamp invalidates every cookie already issued to this
                // account; IsActive alone would not, because the session cookie is only
                // re-checked against the database when the stamp is validated.
                // UpdateSecurityStampAsync changes the value on the user, UpdateAsync writes it.
                await users.UpdateSecurityStampAsync(user);
                await users.UpdateAsync(user);
                await db.Assignments.Where(x => (x.TeacherId == id || x.StudentId == id) && x.IsActive).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            }
            var changedFields = string.Join(",", new[] { email is not null ? "email" : null, name is not null ? "name" : null, input.IsActive is not null ? "isActive" : null, input.Password is not null ? "password" : null }.Where(x => x is not null));
            Audit(db, UserId(principal), "admin_user_updated", "User", id, $"fields={changedFields}");
            await db.SaveChangesAsync();
            await realtime.ToUserAsync(id, RealtimeScopes.Self);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Ok(ToUser(user, role));
        });
        // Deletes are soft: lessons keep their historical teacher/student owners.
        admin.MapDelete("/users/{id:guid}", async (Guid id, ClaimsPrincipal principal, UserManager<AcademyUser> users, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var role = await RoleOf(users, user);
            if (role is not ("teacher" or "student" or "support")) return Results.BadRequest(new { message = "Only teacher, student and support accounts can be deactivated here." });
            var now = DateTimeOffset.UtcNow;
            var upcoming = await db.Lessons.AnyAsync(x => x.Status != "cancelled" && x.EndsAt > now && (x.TeacherId == id || x.StudentId == id));
            if (upcoming) return Results.Conflict(new { message = "This account still has lessons scheduled. Cancel those lessons before deactivating the account." });
            var pending = await db.LessonRequests.Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability)
                .Where(x => (x.TeacherId == id || x.StudentId == id) && (x.Status == "pending_teacher" || x.Status == "pending_student" || x.Status == "pending_admin")).ToListAsync();
            foreach (var request in pending)
            {
                request.Status = "rejected";
                request.RejectionReason = "The account was deactivated by an administrator.";
                if (request.AvailabilitySlot is not null) request.AvailabilitySlot.Status = "available";
                if (request.StudentAvailability is not null) request.StudentAvailability.Status = "available";
            }
            user.IsActive = false;
            await users.UpdateSecurityStampAsync(user);
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded) return Results.ValidationProblem(updated.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            Audit(db, UserId(principal), "admin_user_deactivated", "User", id, $"role={role}; pendingRequestsRejected={pending.Count}");
            await db.SaveChangesAsync();
            await db.Assignments.Where(x => (x.TeacherId == id || x.StudentId == id) && x.IsActive).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            await db.AvailabilitySlots.Where(x => x.TeacherId == id && x.Status == "available" && x.StartsAt > now).ExecuteDeleteAsync();
            await realtime.ToUserAsync(id, RealtimeScopes.Self);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.NoContent();
        });
        admin.MapGet("/support-users", async (UserManager<AcademyUser> users) =>
        {
            var list = await users.GetUsersInRoleAsync("support");
            return Results.Ok(list.OrderBy(x => x.FullName).Select(x => ToUser(x, "support")).ToList());
        });
        admin.MapGet("/teachers/{teacherId:guid}/availability", async (Guid teacherId, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var teacher = await users.FindByIdAsync(teacherId.ToString());
            if (teacher is null || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            return Results.Ok(await db.AvailabilitySlots.Where(x => x.TeacherId == teacherId && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync());
        });
        admin.MapPost("/teachers/{teacherId:guid}/availability", async (Guid teacherId, PublishAvailabilityRequest input, UserManager<AcademyUser> users, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var teacher = await users.FindByIdAsync(teacherId.ToString());
            if (teacher is null || !teacher.IsActive || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt) return Results.BadRequest(new { message = "Choose a future slot with an end after the start." });
            if (!HasAllowedLessonLength(input.EndsAt - input.StartsAt)) return Results.BadRequest(new { message = LessonLengthMessage });
            var conflict = await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == teacherId && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.Lessons.AnyAsync(x => x.TeacherId == teacherId && x.Status == "scheduled" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (conflict) return Results.Conflict(new { message = "This time overlaps another published slot or lesson." });
            var slot = new AvailabilitySlot { TeacherId = teacherId, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.AvailabilitySlots.Add(slot); await db.SaveChangesAsync();
            await realtime.ToUserAsync(teacherId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/admin/teachers/{teacherId}/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        admin.MapDelete("/teachers/{teacherId:guid}/availability/{slotId:guid}", async (Guid teacherId, Guid slotId, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var slot = await db.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId);
            if (slot is null) return Results.NotFound();
            if (slot.Status != "available") return Results.Conflict(new { message = "A requested or booked slot cannot be deleted." });
            db.AvailabilitySlots.Remove(slot); await db.SaveChangesAsync();
            await realtime.ToUserAsync(teacherId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.NoContent();
        });
        admin.MapGet("/contact-submissions", async (AcademyDbContext db) => await db.ContactSubmissions.OrderByDescending(x => x.CreatedAt).ToListAsync());
        admin.MapGet("/lesson-requests", async (string? status, AcademyDbContext db) =>
        {
            var q = db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            return Results.Ok((await q.OrderBy(x => x.CreatedAt).ToListAsync()).Select(ToRequest));
        });
        admin.MapPost("/lesson-requests/{id:guid}/approve", async (Guid id, AcademyDbContext db, RealtimeNotifier realtime) => await ApproveRequest(id, db, realtime));
        admin.MapPost("/lesson-requests/{id:guid}/reject", async (Guid id, AcademyDbContext db, RealtimeNotifier realtime, RejectRequest? input = null) => await RejectRequestByAdmin(id, input?.Reason, db, realtime));
        admin.MapGet("/change-requests", async (string? status, AcademyDbContext db) =>
        {
            var q = ChangeRequestsWithDetails(db);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            return Results.Ok((await q.OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToChangeRequest));
        });
        admin.MapGet("/teachers/{teacherId:guid}/availability-rules", async (Guid teacherId, AcademyDbContext db) =>
            await db.Users.AnyAsync(x => x.Id == teacherId)
                ? Results.Ok(await WeeklyRulesAsync(db, teacherId, null))
                : Results.NotFound());
        admin.MapPost("/teachers/{teacherId:guid}/availability-rules", async (Guid teacherId, CreateAvailabilityRulesRequest input, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == teacherId)) return Results.NotFound();
            var (created, error) = await CreateAvailabilityRulesAsync(teacherId, input, db);
            if (error is not null) return Results.BadRequest(new { message = error });
            await realtime.ToUserAsync(teacherId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/admin/teachers/{teacherId}/availability-rules", created!.Select(x => ToRule(x, "available")).ToList());
        });
        admin.MapDelete("/teachers/{teacherId:guid}/availability-rules/{ruleId:guid}", async (Guid teacherId, Guid ruleId, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var result = await DeleteAvailabilityRuleAsync(teacherId, ruleId, db);
            await realtime.ToUserAsync(teacherId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return result;
        });
        admin.MapPost("/teachers/{teacherId:guid}/availability-rules/{ruleId:guid}/release", async (Guid teacherId, Guid ruleId, ClaimsPrincipal principal, AcademyDbContext db, RealtimeNotifier realtime) =>
            await ReleaseAvailabilityRuleAsync(teacherId, ruleId, UserId(principal), db, realtime));

        // Transcript review lives in LessonReviewEndpoints (also open to support).

        admin.MapGet("/assignments", async (AcademyDbContext db) => await AssignmentList(db));
        api.MapGet("/teacher-student-assignments", async (AcademyDbContext db) => await AssignmentList(db)).RequireAuthorization("AdminOnly");
        api.MapPost("/teacher-student-assignments", async (AssignStudentRequest input, AcademyDbContext db, UserManager<AcademyUser> users, RealtimeNotifier realtime) =>
        {
            var teacher = await users.FindByIdAsync(input.TeacherId.ToString());
            var student = await users.FindByIdAsync(input.StudentId.ToString());
            if (teacher is null || student is null || !await users.IsInRoleAsync(teacher, "teacher") || !await users.IsInRoleAsync(student, "student")) return Results.BadRequest(new { message = "A valid teacher and student are required." });
            var activeElsewhere = await db.Assignments.Where(x => x.StudentId == input.StudentId && x.IsActive && x.TeacherId != input.TeacherId).ToListAsync();
            foreach (var previous in activeElsewhere) previous.IsActive = false;
            var existing = await db.Assignments.FirstOrDefaultAsync(x => x.TeacherId == input.TeacherId && x.StudentId == input.StudentId);
            if (existing is not null)
            {
                existing.IsActive = true; await db.SaveChangesAsync();
                await realtime.ToParticipantsAsync(input.TeacherId, input.StudentId, RealtimeScopes.Directory);
                await realtime.ToStaffAsync(RealtimeScopes.Directory);
                return Results.Ok(await AssignmentById(db, existing.Id));
            }
            var assignment = new TeacherStudentAssignment { TeacherId = input.TeacherId, StudentId = input.StudentId };
            db.Assignments.Add(assignment); await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(input.TeacherId, input.StudentId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/teacher-student-assignments/{assignment.Id}", await AssignmentById(db, assignment.Id));
        }).RequireAuthorization("AdminOnly");
        api.MapDelete("/teacher-student-assignments/{id:guid}", async (Guid id, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var item = await db.Assignments.FindAsync(id); if (item is null) return Results.NotFound();
            item.IsActive = false; await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(item.TeacherId, item.StudentId, RealtimeScopes.Directory);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        api.MapGet("/teachers", async (bool? includeInactive, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var list = await users.GetUsersInRoleAsync("teacher"); return Results.Ok(list.Where(x => x.IsActive || includeInactive == true).Select(x => ToUser(x, "teacher")));
        }).RequireAuthorization("AdminOnly");
        api.MapGet("/teachers/{id:guid}", async (Guid id, UserManager<AcademyUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString()); return user is null || !await users.IsInRoleAsync(user, "teacher") ? Results.NotFound() : Results.Ok(ToUser(user, "teacher"));
        }).RequireAuthorization("AdminOnly");

        // Student-facing directory of active teachers and their open lesson times.
        api.MapGet("/teachers/directory", async (AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teachers = (await users.GetUsersInRoleAsync("teacher")).Where(x => x.IsActive).ToList();
            var teacherIds = teachers.Select(x => x.Id).ToList();
            var slots = await db.AvailabilitySlots.AsNoTracking()
                .Where(x => teacherIds.Contains(x.TeacherId) && x.Status == "available" && x.StartsAt > DateTimeOffset.UtcNow)
                .OrderBy(x => x.StartsAt)
                .ToListAsync();
            var ratings = await db.TeacherReviews.AsNoTracking()
                .Where(x => teacherIds.Contains(x.TeacherId))
                .GroupBy(x => x.TeacherId)
                .Select(g => new { TeacherId = g.Key, Average = (double?)g.Average(x => (double)x.Rating), Count = g.Count() })
                .ToDictionaryAsync(x => x.TeacherId);
            var result = teachers
                .OrderBy(x => x.FullName)
                .Select(teacher =>
                {
                    ratings.TryGetValue(teacher.Id, out var rating);
                    return new TeacherAvailabilityDto(
                        ToPerson(teacher, "teacher"),
                        slots.Where(slot => slot.TeacherId == teacher.Id)
                            .Select(slot => new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status))
                            .ToList(),
                        rating?.Average,
                        rating?.Count ?? 0);
                })
                .ToList();
            return Results.Ok(result);
        }).RequireAuthorization();

        // Student-facing directory of teachers and their announced weekly times.
        api.MapGet("/teachers/weekly-directory", async (AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teachers = (await users.GetUsersInRoleAsync("teacher")).Where(x => x.IsActive).OrderBy(x => x.FullName).ToList();
            var teacherIds = teachers.Select(x => x.Id).ToList();
            var ratings = await db.TeacherReviews.AsNoTracking()
                .Where(x => teacherIds.Contains(x.TeacherId))
                .GroupBy(x => x.TeacherId)
                .Select(g => new { TeacherId = g.Key, Average = (double?)g.Average(x => (double)x.Rating), Count = g.Count() })
                .ToDictionaryAsync(x => x.TeacherId);
            var result = new List<WeeklyTeacherDto>();
            foreach (var teacher in teachers)
            {
                ratings.TryGetValue(teacher.Id, out var rating);
                result.Add(new WeeklyTeacherDto(
                    ToPerson(teacher, "teacher"),
                    await WeeklyRulesAsync(db, teacher.Id, null),
                    rating?.Average,
                    rating?.Count ?? 0));
            }
            return Results.Ok(result);
        }).RequireAuthorization();

        api.MapGet("/teachers/{id:guid}/profile", async (Guid id, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teacher = await users.FindByIdAsync(id.ToString());
            if (teacher is null || !teacher.IsActive || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            var slots = await db.AvailabilitySlots.AsNoTracking()
                .Where(x => x.TeacherId == id && x.Status == "available" && x.StartsAt > DateTimeOffset.UtcNow)
                .OrderBy(x => x.StartsAt)
                .Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status))
                .ToListAsync();
            var reviews = await db.TeacherReviews.AsNoTracking()
                .Include(x => x.Lesson).Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => x.TeacherId == id)
                .OrderByDescending(x => x.CreatedAt)
                .Take(20)
                .ToListAsync();
            var reviewCount = await db.TeacherReviews.CountAsync(x => x.TeacherId == id);
            var average = reviewCount == 0 ? (double?)null : await db.TeacherReviews.Where(x => x.TeacherId == id).AverageAsync(x => (double)x.Rating);
            return Results.Ok(new TeacherAvailabilityDto(ToPerson(teacher, "teacher"), slots, average, reviewCount, reviews.Select(ToReview).ToList()));
        }).RequireAuthorization();

        api.MapGet("/teachers/public", async (AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teachers = (await users.GetUsersInRoleAsync("teacher")).Where(x => x.IsActive).OrderBy(x => x.FullName).ToList();
            var ids = teachers.Select(x => x.Id).ToList();
            var reviews = await db.TeacherReviews.AsNoTracking()
                .Include(x => x.Lesson).Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => ids.Contains(x.TeacherId))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            var result = teachers.Select(teacher =>
            {
                var teacherReviews = reviews.Where(x => x.TeacherId == teacher.Id).ToList();
                var average = teacherReviews.Count == 0 ? (double?)null : teacherReviews.Average(x => x.Rating);
                return new PublicTeacherDto(
                    teacher.Id,
                    DisplayName(teacher.FullName) ?? "Teacher",
                    $"/api/users/{teacher.Id}/avatar",
                    average,
                    teacherReviews.Count,
                    teacherReviews.Take(3).Select(ToReview).ToList());
            }).ToList();
            return Results.Ok(result);
        }).AllowAnonymous();

        api.MapGet("/teachers/public/{id:guid}", async (Guid id, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teacher = await users.FindByIdAsync(id.ToString());
            if (teacher is null || !teacher.IsActive || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            var reviews = await db.TeacherReviews.AsNoTracking()
                .Include(x => x.Lesson).Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => x.TeacherId == id)
                .OrderByDescending(x => x.CreatedAt)
                .Take(50)
                .ToListAsync();
            var reviewCount = await db.TeacherReviews.CountAsync(x => x.TeacherId == id);
            var average = reviewCount == 0 ? (double?)null : await db.TeacherReviews.Where(x => x.TeacherId == id).AverageAsync(x => (double)x.Rating);
            return Results.Ok(new PublicTeacherDto(teacher.Id, DisplayName(teacher.FullName) ?? "Teacher", $"/api/users/{teacher.Id}/avatar", average, reviewCount, reviews.Select(ToReview).ToList()));
        }).AllowAnonymous();
        api.MapGet("/students", async (bool? includeInactive, ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            if (!IsAdmin(p) && !HasRole(p, "teacher")) return Results.Forbid();
            var includeInactiveForAdmin = includeInactive == true && IsAdmin(p);
            var q = users.Users.Where(x => x.IsActive || includeInactiveForAdmin);
            if (HasRole(p, "teacher")) { var id = UserId(p); var studentIds = db.Assignments.Where(a => a.TeacherId == id && a.IsActive).Select(a => a.StudentId); q = q.Where(u => studentIds.Contains(u.Id)); }
            var list = await q.ToListAsync(); var result = new List<UserDto>();
            foreach (var user in list) if (await users.IsInRoleAsync(user, "student")) result.Add(IsAdmin(p) ? ToUser(user, "student") : ToPerson(user, "student"));
            return Results.Ok(result);
        }).RequireAuthorization();
        api.MapPost("/students", async (CreateStudentRequest input, UserManager<AcademyUser> users, RealtimeNotifier realtime) =>
        {
            var grade = string.IsNullOrWhiteSpace(input.Grade) ? null : input.Grade.Trim();
            if (string.IsNullOrWhiteSpace(input.FullName) || !System.Net.Mail.MailAddress.TryCreate(input.Email, out _) || string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["student"] = ["Full name, a valid email and a password of at least 8 characters are required."] });
            if (grade is not null && Array.IndexOf(StudentGrades, grade) < 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["grade"] = ["Grade must be Kindergarten or one of Grade 1 to Grade 12."] });
            if (await users.FindByEmailAsync(input.Email.Trim()) is not null) return Results.Conflict(new { message = "An account with this email already exists." });
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), Grade = grade, EmailConfirmed = true };
            var created = await users.CreateAsync(user, input.Password);
            if (!created.Succeeded) return Results.ValidationProblem(IdentityErrors(created));
            await users.AddToRoleAsync(user, "student");
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/students/{user.Id}", ToUser(user, "student"));
        }).RequireAuthorization("AdminOnly");
        api.MapGet("/students/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString()); if (user is null || !await users.IsInRoleAsync(user, "student")) return Results.NotFound();
            if (HasRole(p, "teacher") && !await db.Assignments.AnyAsync(a => a.TeacherId == UserId(p) && a.StudentId == id && a.IsActive)) return Results.Forbid();
            if (!IsAdmin(p) && !HasRole(p, "teacher") && UserId(p) != id) return Results.Forbid();
            // Staff and the student themself keep the contact fields; a teacher
            // sees who the student is, not how to reach them.
            var canSeeContact = IsAdmin(p) || UserId(p) == id;
            return Results.Ok(canSeeContact ? ToUser(user, "student") : ToPerson(user, "student"));
        }).RequireAuthorization();

        var teacher = api.MapGroup("/teacher").RequireAuthorization("TeacherOnly");
        teacher.MapGet("/availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.AvailabilitySlots.Where(x => x.TeacherId == UserId(p) && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync()));
        teacher.MapPost("/availability", async (PublishAvailabilityRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt) return Results.BadRequest(new { message = "Choose a future slot with an end after the start." });
            if (!HasAllowedLessonLength(input.EndsAt - input.StartsAt)) return Results.BadRequest(new { message = LessonLengthMessage });
            var id = UserId(p);
            var conflict = await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == id && x.Status != "available" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.Lessons.AnyAsync(x => x.TeacherId == id && x.Status == "scheduled" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == id && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (conflict) return Results.Conflict(new { message = "This time overlaps another published slot or lesson." });
            var slot = new AvailabilitySlot { TeacherId = id, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.AvailabilitySlots.Add(slot); await db.SaveChangesAsync();
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            await realtime.ToUserAsync(id, RealtimeScopes.Directory);
            return Results.Created($"/api/teacher/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        teacher.MapDelete("/availability/{slotId:guid}", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var slot = await db.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == slotId && x.TeacherId == UserId(p));
            if (slot is null) return Results.NotFound(); if (slot.Status != "available") return Results.Conflict(new { message = "A requested or booked slot cannot be deleted." });
            db.AvailabilitySlots.Remove(slot); await db.SaveChangesAsync();
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            await realtime.ToUserAsync(UserId(p), RealtimeScopes.Directory);
            return Results.NoContent();
        });

        // Weekly availability announcement: one row per bookable weekly slot.
        teacher.MapGet("/availability-rules", async (ClaimsPrincipal p, AcademyDbContext db) =>
            Results.Ok(await WeeklyRulesAsync(db, UserId(p), null)));
        teacher.MapPost("/availability-rules", async (CreateAvailabilityRulesRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var (created, error) = await CreateAvailabilityRulesAsync(UserId(p), input, db);
            if (error is not null) return Results.BadRequest(new { message = error });
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created("/api/teacher/availability-rules", created!.Select(x => ToRule(x, "available")).ToList());
        });
        teacher.MapDelete("/availability-rules/{ruleId:guid}", async (Guid ruleId, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var result = await DeleteAvailabilityRuleAsync(UserId(p), ruleId, db);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return result;
        });
        teacher.MapGet("/lesson-requests", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok((await db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).Where(x => x.TeacherId == UserId(p)).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToRequest)));
        teacher.MapPost("/lesson-requests/{id:guid}/confirm", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var request = await db.LessonRequests.Include(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_teacher") return Results.Conflict(new { message = "This request is no longer awaiting teacher confirmation." });
            request.Status = "pending_admin"; request.TeacherRespondedAt = DateTimeOffset.UtcNow;
            db.Notifications.Add(new AppNotification
            {
                UserId = request.StudentId,
                Title = $"{request.Teacher?.FullName ?? "Your teacher"} accepted your lesson request",
                Body = "They agreed to the time. The academy will confirm it on your schedule.",
                Kind = "request",
                Link = "/schedule",
                DedupeKey = $"request-teacher-accepted:{request.Id}",
            });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(request.StudentId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Ok(await RequestById(db, id));
        });
        teacher.MapPost("/lesson-requests/{id:guid}/reject", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime, RejectRequest? input = null) =>
        {
            var request = await db.LessonRequests.Include(x => x.Teacher).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_teacher") return Results.Conflict(new { message = "This request is no longer awaiting teacher confirmation." });
            request.Status = "rejected"; request.RejectionReason = input?.Reason; request.TeacherRespondedAt = DateTimeOffset.UtcNow; request.AvailabilitySlot!.Status = "available";
            db.Notifications.Add(new AppNotification
            {
                UserId = request.StudentId,
                Title = $"{request.Teacher?.FullName ?? "Your teacher"} declined your lesson request",
                Body = input?.Reason ?? "Pick another weekly slot to keep learning.",
                Kind = "request",
                Link = "/book",
                DedupeKey = $"request-teacher-declined:{request.Id}",
            });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(request.StudentId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Ok(await RequestById(db, id));
        });

        api.MapGet("/student/teacher/availability", async (ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var studentId = UserId(p);
            var assignment = await db.Assignments.Include(x => x.Teacher).Where(x => x.StudentId == studentId && x.IsActive).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            if (assignment?.Teacher is null) return Results.Ok(new { teacher = (UserDto?)null, slots = Array.Empty<AvailabilityDto>(), averageRating = (double?)null, reviewCount = 0, reviews = Array.Empty<TeacherReviewDto>() });
            var slots = await db.AvailabilitySlots.Where(x => x.TeacherId == assignment.TeacherId && x.Status == "available" && x.StartsAt > DateTimeOffset.UtcNow).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync();
            var rating = await TeacherRatingAsync(db, assignment.TeacherId);
            var reviews = await db.TeacherReviews.AsNoTracking().Include(x => x.Lesson).Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => x.TeacherId == assignment.TeacherId)
                .OrderByDescending(x => x.CreatedAt).Take(5).ToListAsync();
            return Results.Ok(new TeacherAvailabilityDto(ToPerson(assignment.Teacher, "teacher"), slots, rating.Average, rating.Count, reviews.Select(x => ToReview(x)).ToList()));
        }).RequireAuthorization("StudentOnly");

        // Weekly availability: the teacher announces recurring slots once and
        // students book them directly. No teacher or admin confirmation step.
        api.MapGet("/student/teacher/weekly-availability", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var studentId = UserId(p);
            var assignment = await db.Assignments.Include(x => x.Teacher).Where(x => x.StudentId == studentId && x.IsActive).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            if (assignment?.Teacher is null) return Results.Ok(new WeeklyAvailabilityDto(null, [], []));
            return Results.Ok(await WeeklyAvailabilityAsync(db, assignment.Teacher, studentId));
        }).RequireAuthorization("StudentOnly");
        api.MapGet("/teachers/{id:guid}/weekly-availability", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teacher = await users.FindByIdAsync(id.ToString());
            if (teacher is null || !teacher.IsActive || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            return Results.Ok(await WeeklyAvailabilityAsync(db, teacher, UserId(p)));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/student/bookings", async (BookWeeklySlotsRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            var ruleIds = (input.RuleIds ?? []).Distinct().ToList();
            if (ruleIds.Count < 1)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["ruleIds"] = ["Choose at least one weekly slot."] });
            var rules = await db.TeacherAvailabilityRules.Where(x => ruleIds.Contains(x.Id))
                .OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartsAtTime).ToListAsync();
            if (rules.Count != ruleIds.Count) return Results.Conflict(new { message = "One of the chosen slots no longer exists." });
            if (rules.Select(x => x.TeacherId).Distinct().Count() != 1)
                return Results.Conflict(new { message = "Choose slots from a single teacher." });
            var teacherId = rules[0].TeacherId;
            if (!await db.Users.AnyAsync(x => x.Id == teacherId && x.IsActive))
                return Results.Conflict(new { message = "This teacher is not accepting lessons right now." });
            var taken = await db.LessonSeries.Where(x => x.IsActive && ruleIds.Contains(x.AvailabilityRuleId))
                .Select(x => x.AvailabilityRuleId).ToListAsync();
            if (taken.Count > 0)
                return Results.Conflict(new { message = "One of the chosen slots was just booked. Refresh and pick another time." });
            var mySeries = await db.LessonSeries.Where(x => x.StudentId == studentId && x.IsActive).ToListAsync();
            if (rules.Any(r => mySeries.Any(s => Overlaps(s.DayOfWeek, s.StartsAtTime, s.EndsAtTime, r.DayOfWeek, r.StartsAtTime, r.EndsAtTime))))
                return Results.Conflict(new { message = "One of the chosen slots overlaps a lesson you already have." });
            var now = DateTimeOffset.UtcNow;
            foreach (var rule in rules)
                foreach (var (start, end) in NextOccurrences(rule.DayOfWeek, rule.StartsAtTime, rule.EndsAtTime, ResolveTimeZone(rule.TimeZoneId), now, 8))
                    if (await db.Lessons.AnyAsync(x => x.Status == "scheduled" && x.StartsAt < end && x.EndsAt > start && (x.TeacherId == teacherId || x.StudentId == studentId)))
                        return Results.Conflict(new { message = "This time conflicts with an existing lesson." });

            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Assignments.Where(x => x.StudentId == studentId && x.IsActive && x.TeacherId != teacherId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            var assignment = await db.Assignments.FirstOrDefaultAsync(x => x.StudentId == studentId && x.TeacherId == teacherId);
            if (assignment is null) db.Assignments.Add(new TeacherStudentAssignment { TeacherId = teacherId, StudentId = studentId });
            else if (!assignment.IsActive) assignment.IsActive = true;

            var firstEverLesson = !await db.Lessons.AnyAsync(x => x.StudentId == studentId);
            var series = rules.Select(rule => new LessonSeries
            {
                TeacherId = teacherId,
                StudentId = studentId,
                AvailabilityRuleId = rule.Id,
                DayOfWeek = rule.DayOfWeek,
                StartsAtTime = rule.StartsAtTime,
                EndsAtTime = rule.EndsAtTime,
                TimeZoneId = rule.TimeZoneId,
            }).ToList();
            db.LessonSeries.AddRange(series);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { await tx.RollbackAsync(); return Results.Conflict(new { message = "One of the chosen slots was just booked. Refresh and pick another time." }); }

            var created = new List<Lesson>();
            foreach (var s in series) created.AddRange(await BuildSeriesLessonsAsync(db, s, now));
            if (firstEverLesson && created.Count > 0) created.OrderBy(x => x.StartsAt).First().IsTrial = true;
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // Booking a weekly time makes the student assigned, so any open
            // broadcast offers can no longer be claimed.
            await db.OpenStudentOffers.Where(x => x.StudentId == studentId && x.Status == "open")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "withdrawn"));

            var seriesIds = series.Select(x => x.Id).ToList();
            var lessonIds = created.Select(x => x.Id).ToList();
            var savedSeries = await db.LessonSeries.Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => seriesIds.Contains(x.Id)).ToListAsync();
            var savedLessons = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review)
                .Where(x => lessonIds.Contains(x.Id)).OrderBy(x => x.StartsAt).ToListAsync();

            // The teacher must hear about a new booking right away: the slot is
            // already theirs, so the notice is the first sign on their schedule.
            var studentName = savedSeries[0].Student?.FullName ?? "A student";
            var teacherName = savedSeries[0].Teacher?.FullName ?? "your teacher";
            var bookingKey = $"booking:{savedSeries[0].Id}";
            var timeWord = savedSeries.Count == 1 ? "weekly time" : "weekly times";
            var firstAt = savedLessons.Count > 0
                ? LessonOpsMessaging.When(savedLessons[0].StartsAt, savedSeries[0].TimeZoneId)
                : null;
            db.Notifications.AddRange(
                new AppNotification
                {
                    UserId = teacherId,
                    Title = "New weekly booking",
                    Body = firstAt is null
                        ? $"{studentName} booked {savedSeries.Count} {timeWord} with you. The first lesson is on the way."
                        : $"{studentName} booked {savedSeries.Count} {timeWord} with you. First lesson {firstAt}.",
                    Kind = "booking",
                    Link = "/schedule",
                    DedupeKey = bookingKey,
                },
                new AppNotification
                {
                    UserId = studentId,
                    Title = "Weekly lessons booked",
                    Body = firstAt is null
                        ? $"Your {savedSeries.Count} {timeWord} with {teacherName} repeat every week. Open your schedule to see the first lesson."
                        : $"Your {savedSeries.Count} {timeWord} with {teacherName} repeat every week. First lesson {firstAt}.",
                    Kind = "booking",
                    Link = "/schedule",
                    DedupeKey = $"{bookingKey}:student",
                });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* The booking is already saved; a repeated notice is harmless. */ }

            await realtime.ToParticipantsAsync(teacherId, studentId, RealtimeScopes.Lessons);
            await realtime.ToUsersAsync([teacherId, studentId], RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);

            return Results.Created("/api/student/series", new { series = savedSeries.Select(ToSeries).ToList(), lessons = savedLessons.Select(ToLesson).ToList() });
        }).RequireAuthorization("StudentOnly");
        api.MapGet("/student/series", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var studentId = UserId(p);
            var list = await db.LessonSeries.Include(x => x.Teacher).Include(x => x.Student)
                .Where(x => x.StudentId == studentId && x.IsActive)
                .OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartsAtTime).ToListAsync();
            return Results.Ok(list.Select(ToSeries));
        }).RequireAuthorization("StudentOnly");
        api.MapDelete("/student/series/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            var series = await db.LessonSeries.Include(x => x.Student).Include(x => x.Teacher)
                .FirstOrDefaultAsync(x => x.Id == id && x.StudentId == studentId && x.IsActive);
            if (series is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync();
            series.IsActive = false;

            // A series has no end date, so future lessons are already materialized
            // on the schedule. Ending the weekly time means releasing them.
            var futureLessons = await db.Lessons
                .Where(x => x.SeriesId == series.Id && x.Status == "scheduled" && x.StartsAt > now).ToListAsync();
            foreach (var lesson in futureLessons)
            {
                lesson.Status = "cancelled";
                lesson.CancellationOutcome = "series_ended";
                lesson.CancelReason = "The student ended this weekly time.";
                lesson.CancelledById = studentId;
                lesson.CancelledAt = now;
            }

            var keepsTeacher = await db.LessonSeries.AnyAsync(x =>
                x.StudentId == studentId && x.TeacherId == series.TeacherId && x.IsActive && x.Id != series.Id);
            if (!keepsTeacher)
                await db.Assignments.Where(x => x.StudentId == studentId && x.TeacherId == series.TeacherId && x.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));

            var studentName = series.Student?.FullName ?? "A student";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                series.TeacherId,
                "Weekly lessons ended",
                $"{studentName} ended the weekly lessons with you on {LessonOpsMessaging.WeeklyAt(series.DayOfWeek, series.StartsAtTime, series.TimeZoneId)}. The time is now open for other students to book.",
                "booking",
                "/schedule",
                $"series-ended:{series.Id}"));

            try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
            catch (DbUpdateException) { await tx.RollbackAsync(); return Results.Conflict(new { message = "Unable to end this weekly time. Please try again." }); }

            await realtime.ToParticipantsAsync(series.TeacherId, studentId, RealtimeScopes.Lessons);
            await realtime.ToUsersAsync([series.TeacherId, studentId], RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.NoContent();
        }).RequireAuthorization("StudentOnly");
        api.MapGet("/student/change-requests", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var studentId = UserId(p);
            var list = await ChangeRequestsWithDetails(db).Where(x => x.StudentId == studentId)
                .OrderByDescending(x => x.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(ToChangeRequest));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/student/series/{id:guid}/change-requests", async (Guid id, CreateLessonChangeRequestInput input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime, AcademyPushSender push) =>
        {
            var studentId = UserId(p);
            var series = await db.LessonSeries.Include(x => x.Teacher).Include(x => x.Student)
                .FirstOrDefaultAsync(x => x.Id == id && x.StudentId == studentId && x.IsActive);
            if (series is null) return Results.NotFound();
            var note = input.Note?.Trim();
            if (note is { Length: > 500 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Keep the note under 500 characters."] });
            var rule = await db.TeacherAvailabilityRules.FirstOrDefaultAsync(x => x.Id == input.RequestedRuleId && x.TeacherId == series.TeacherId);
            if (rule is null) return Results.NotFound();
            if (rule.Id == series.AvailabilityRuleId) return Results.Conflict(new { message = "That is already your booked time." });

            var now = DateTimeOffset.UtcNow;
            var request = new LessonChangeRequest
            {
                SeriesId = series.Id,
                StudentId = studentId,
                TeacherId = series.TeacherId,
                RequestedRuleId = rule.Id,
                StudentNote = note is { Length: > 0 } ? note : null,
            };
            if (await RecordTimeChangeAsync(db, series, rule, request, now, push) is { } blocked)
                return Results.Conflict(new { message = blocked });

            await realtime.ToParticipantsAsync(series.TeacherId, studentId, RealtimeScopes.Lessons);
            await realtime.ToUsersAsync([series.TeacherId, studentId], RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            var saved = await ChangeRequestsWithDetails(db).FirstAsync(x => x.Id == request.Id);
            return Results.Created($"/api/student/change-requests/{request.Id}", ToChangeRequest(saved));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/lesson-requests", async (RequestSlotInput input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            if (await db.LessonRequests.AnyAsync(x => x.StudentId == studentId && (x.Status == "pending_teacher" || x.Status == "pending_student" || x.Status == "pending_admin"))) return Results.Conflict(new { message = "You already have a lesson request awaiting approval." });
            var slot = await db.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == input.AvailabilitySlotId);
            if (slot is null || slot.Status != "available" || slot.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "This slot is no longer available." });
            if (!await db.Users.AnyAsync(x => x.Id == slot.TeacherId && x.IsActive)) return Results.Conflict(new { message = "This teacher is not accepting lessons right now." });
            await using var tx = await db.Database.BeginTransactionAsync();
            var claimed = await db.AvailabilitySlots.Where(x => x.Id == slot.Id && x.Status == "available").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "requested"));
            if (claimed == 0) return Results.Conflict(new { message = "This slot was just requested by someone else." });
            // Self-service booking: selecting any teacher's slot makes that teacher the student's active teacher.
            await db.Assignments.Where(x => x.StudentId == studentId && x.IsActive && x.TeacherId != slot.TeacherId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            var assignment = await db.Assignments.FirstOrDefaultAsync(x => x.StudentId == studentId && x.TeacherId == slot.TeacherId);
            if (assignment is null) db.Assignments.Add(new TeacherStudentAssignment { TeacherId = slot.TeacherId, StudentId = studentId });
            else if (!assignment.IsActive) assignment.IsActive = true;
            var request = new LessonRequest { TeacherId = slot.TeacherId, StudentId = studentId, AvailabilitySlotId = slot.Id, Status = "pending_teacher" };
            db.LessonRequests.Add(request);
            var requesterName = await db.Users.Where(x => x.Id == studentId).Select(x => x.FullName).FirstOrDefaultAsync() ?? "A student";
            db.Notifications.Add(new AppNotification
            {
                UserId = slot.TeacherId,
                Title = "New lesson request",
                Body = $"{requesterName} requested one of your lesson times. It appears on your schedule once the academy confirms it.",
                Kind = "request",
                Link = "/schedule",
                DedupeKey = $"request:{request.Id}",
            });
            try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
            catch (DbUpdateException) { await tx.RollbackAsync(); await db.AvailabilitySlots.Where(x => x.Id == slot.Id && x.Status == "requested").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "available")); return Results.Conflict(new { message = "A request is already active or the slot was claimed." }); }
            await realtime.ToParticipantsAsync(slot.TeacherId, studentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(slot.TeacherId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            await realtime.ToStaffAsync(RealtimeScopes.Directory);
            return Results.Created($"/api/lesson-requests/{request.Id}", await RequestById(db, request.Id));
        }).RequireAuthorization("StudentOnly");
        api.MapGet("/student/lesson-requests", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok((await db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).Where(x => x.StudentId == UserId(p)).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToRequest))).RequireAuthorization("StudentOnly");

        var studentApi = api.MapGroup("/student").RequireAuthorization("StudentOnly");
        studentApi.MapGet("/availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.StudentAvailabilities.Where(x => x.StudentId == UserId(p) && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync()));
        studentApi.MapPost("/availability", async (PublishAvailabilityRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt) return Results.BadRequest(new { message = "Choose a future preferred time with an end after the start." });
            if (!HasAllowedLessonLength(input.EndsAt - input.StartsAt)) return Results.BadRequest(new { message = LessonLengthMessage });
            var assignment = await db.Assignments.Where(x => x.StudentId == studentId && x.IsActive).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            if (assignment is null) return Results.Conflict(new { message = "You do not have an assigned teacher yet." });
            var overlaps = await db.StudentAvailabilities.AnyAsync(x => x.StudentId == studentId && x.Status != "rejected" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (overlaps) return Results.Conflict(new { message = "This overlaps another preferred time you have submitted." });
            var slot = new StudentAvailability { StudentId = studentId, TeacherId = assignment.TeacherId, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.StudentAvailabilities.Add(slot); await db.SaveChangesAsync();
            await realtime.ToUserAsync(assignment.TeacherId, RealtimeScopes.Directory);
            await realtime.ToUserAsync(studentId, RealtimeScopes.Directory);
            return Results.Created($"/api/student/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        studentApi.MapDelete("/availability/{slotId:guid}", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var slot = await db.StudentAvailabilities.FirstOrDefaultAsync(x => x.Id == slotId && x.StudentId == UserId(p));
            if (slot is null) return Results.NotFound(); if (slot.Status != "available") return Results.Conflict(new { message = "A selected or booked preference cannot be deleted." });
            db.StudentAvailabilities.Remove(slot); await db.SaveChangesAsync();
            await realtime.ToUserAsync(slot.TeacherId, RealtimeScopes.Directory);
            await realtime.ToUserAsync(UserId(p), RealtimeScopes.Directory);
            return Results.NoContent();
        });
        api.MapGet("/teacher/student-availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.StudentAvailabilities.Include(x => x.Student).Where(x => x.TeacherId == UserId(p) && x.Status == "available" && x.StartsAt > DateTimeOffset.UtcNow).OrderBy(x => x.StartsAt).Select(x => new { x.Id, x.StudentId, studentName = x.Student!.FullName, x.StartsAt, x.EndsAt, x.Status }).ToListAsync())).RequireAuthorization("TeacherOnly");
        api.MapPost("/teacher/student-availability/{slotId:guid}/select", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var teacherId = UserId(p);
            var slot = await db.StudentAvailabilities.FirstOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId);
            if (slot is null || slot.Status != "available" || slot.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "This student preference is no longer available." });
            if (await db.LessonRequests.AnyAsync(x => x.StudentId == slot.StudentId && (x.Status == "pending_teacher" || x.Status == "pending_student" || x.Status == "pending_admin"))) return Results.Conflict(new { message = "This student already has a lesson request awaiting approval." });
            await using var tx = await db.Database.BeginTransactionAsync();
            var claimed = await db.StudentAvailabilities.Where(x => x.Id == slot.Id && x.Status == "available").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "requested"));
            if (claimed == 0) return Results.Conflict(new { message = "This preference was just selected by another action." });
            var request = new LessonRequest { TeacherId = teacherId, StudentId = slot.StudentId, StudentAvailabilityId = slot.Id, Status = "pending_student" };
            db.LessonRequests.Add(request);
            try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
            catch (DbUpdateException) { await tx.RollbackAsync(); return Results.Conflict(new { message = "An active request already exists for this student." }); }
            await realtime.ToParticipantsAsync(teacherId, slot.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(slot.StudentId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Created($"/api/lesson-requests/{request.Id}", await RequestById(db, request.Id));
        }).RequireAuthorization("TeacherOnly");

        // Open student offers: an unassigned student posts a time, every active
        // teacher is notified, and the first acceptance books the lesson.
        studentApi.MapGet("/open-offers", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var studentId = UserId(p);
            var now = DateTimeOffset.UtcNow;
            var offers = await db.OpenStudentOffers.AsNoTracking()
                .Where(x => x.StudentId == studentId && x.StartsAt > now.AddDays(-1))
                .OrderBy(x => x.StartsAt)
                .Select(x => new OpenStudentOfferDto(
                    x.Id, x.StudentId, x.Student != null ? x.Student.FullName : "Student",
                    x.StartsAt, x.EndsAt, x.Status, x.ClaimedByTeacherId,
                    x.ClaimedByTeacher != null ? x.ClaimedByTeacher.FullName : null,
                    x.LessonId, x.CreatedAt, x.ClaimedAt))
                .ToListAsync();
            return Results.Ok(offers);
        });
        studentApi.MapPost("/open-offers", async (PublishOpenOfferRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            var now = DateTimeOffset.UtcNow;
            var starts = input.StartsAt.ToUniversalTime();
            var ends = input.EndsAt.ToUniversalTime();
            if (starts <= now || ends <= starts)
                return Results.BadRequest(new { message = "Choose a future time with an end after the start." });
            if (!HasAllowedLessonLength(ends - starts))
                return Results.BadRequest(new { message = LessonLengthMessage });
            if (await db.Assignments.AnyAsync(x => x.StudentId == studentId && x.IsActive))
                return Results.Conflict(new { message = "You already have a teacher. Book a weekly time from the book page instead." });
            if (await db.OpenStudentOffers.CountAsync(x => x.StudentId == studentId && x.Status == "open" && x.StartsAt > now) >= 4)
                return Results.Conflict(new { message = "You can offer up to 4 times. Withdraw one to add another." });
            if (await db.OpenStudentOffers.AnyAsync(x => x.StudentId == studentId && x.Status == "open" && x.StartsAt < ends && x.EndsAt > starts))
                return Results.Conflict(new { message = "This overlaps another time you offered." });
            if (await db.Lessons.AnyAsync(x => x.StudentId == studentId && x.Status == "scheduled" && x.StartsAt < ends && x.EndsAt > starts))
                return Results.Conflict(new { message = "This overlaps a lesson you already have." });

            var offer = new OpenStudentOffer { StudentId = studentId, StartsAt = starts, EndsAt = ends };
            db.OpenStudentOffers.Add(offer);
            await db.SaveChangesAsync();

            var teacherIds = await ActiveTeacherIdsAsync(db);
            var studentName = await db.Users.Where(x => x.Id == studentId).Select(x => x.FullName).FirstOrDefaultAsync() ?? "A student";
            foreach (var teacherId in teacherIds)
                db.Notifications.Add(LessonOpsMessaging.Notice(
                    teacherId,
                    "A student offered a lesson time",
                    $"{studentName} is looking for a teacher and offered a lesson time. The first teacher to accept books it.",
                    "request",
                    "/teacher/availability",
                    $"open-offer-broadcast:{offer.Id}"));
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* The offer is saved; a repeated notice is harmless. */ }
            await realtime.ToUsersAsync(teacherIds, RealtimeScopes.Offers);
            await realtime.ToUsersAsync(teacherIds, RealtimeScopes.Notifications);
            await realtime.ToUserAsync(studentId, RealtimeScopes.Offers);
            return Results.Created($"/api/student/open-offers/{offer.Id}", ToOpenOffer(offer, studentName, null));
        });
        studentApi.MapDelete("/open-offers/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            var offer = await db.OpenStudentOffers.FirstOrDefaultAsync(x => x.Id == id && x.StudentId == studentId);
            if (offer is null) return Results.NotFound();
            if (offer.Status != "open") return Results.Conflict(new { message = "This time is no longer open." });
            var studentName = await db.Users.Where(x => x.Id == studentId).Select(x => x.FullName).FirstOrDefaultAsync() ?? "A student";
            offer.Status = "withdrawn";
            await db.SaveChangesAsync();
            await db.Notifications.Where(x => x.DedupeKey == $"open-offer-broadcast:{offer.Id}")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Title, "Time offer withdrawn")
                    .SetProperty(x => x.Body, $"{studentName} withdrew the time they offered. No action is needed."));
            var teacherIds = await ActiveTeacherIdsAsync(db);
            await realtime.ToUsersAsync(teacherIds, RealtimeScopes.Offers);
            await realtime.ToUsersAsync(teacherIds, RealtimeScopes.Notifications);
            await realtime.ToUserAsync(studentId, RealtimeScopes.Offers);
            return Results.NoContent();
        });
        teacher.MapGet("/open-offers", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var offers = await db.OpenStudentOffers.AsNoTracking()
                .Where(x => (x.Status == "open" && x.StartsAt > now)
                    || (x.Status == "claimed" && x.ClaimedAt > now.AddHours(-24)))
                .OrderBy(x => x.StartsAt).Take(100)
                .Select(x => new OpenStudentOfferDto(
                    x.Id, x.StudentId, x.Student != null ? x.Student.FullName : "Student",
                    x.StartsAt, x.EndsAt, x.Status, x.ClaimedByTeacherId,
                    x.ClaimedByTeacher != null ? x.ClaimedByTeacher.FullName : null,
                    x.LessonId, x.CreatedAt, x.ClaimedAt))
                .ToListAsync();
            return Results.Ok(offers);
        });
        teacher.MapPost("/open-offers/{id:guid}/accept", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var teacherId = UserId(p);
            var now = DateTimeOffset.UtcNow;
            var offer = await db.OpenStudentOffers.Include(x => x.Student).FirstOrDefaultAsync(x => x.Id == id);
            if (offer is null || offer.Status != "open" || offer.StartsAt <= now)
                return Results.Conflict(new { message = "This time is no longer available." });
            if (!await db.Users.AnyAsync(x => x.Id == teacherId && x.IsActive))
                return Results.Conflict(new { message = "Your account is not active." });
            if (await db.Assignments.AnyAsync(x => x.StudentId == offer.StudentId && x.IsActive))
            {
                offer.Status = "withdrawn";
                await db.SaveChangesAsync();
                return Results.Conflict(new { message = "This student already has a teacher." });
            }
            if (await db.Lessons.AnyAsync(x => x.Status == "scheduled" && x.StartsAt < offer.EndsAt && x.EndsAt > offer.StartsAt
                && (x.TeacherId == teacherId || x.StudentId == offer.StudentId)))
                return Results.Conflict(new { message = "This time overlaps a lesson on your schedule or the student's." });

            await using var tx = await db.Database.BeginTransactionAsync();
            var claimed = await db.OpenStudentOffers.Where(x => x.Id == offer.Id && x.Status == "open")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "claimed")
                    .SetProperty(x => x.ClaimedByTeacherId, teacherId)
                    .SetProperty(x => x.ClaimedAt, now));
            if (claimed == 0)
            {
                await tx.RollbackAsync();
                return Results.Conflict(new { message = "Another teacher just accepted this time." });
            }
            offer.Status = "claimed";
            offer.ClaimedByTeacherId = teacherId;
            offer.ClaimedAt = now;

            await db.Assignments.Where(x => x.StudentId == offer.StudentId && x.IsActive && x.TeacherId != teacherId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            var assignment = await db.Assignments.FirstOrDefaultAsync(x => x.StudentId == offer.StudentId && x.TeacherId == teacherId);
            if (assignment is null) db.Assignments.Add(new TeacherStudentAssignment { TeacherId = teacherId, StudentId = offer.StudentId });
            else if (!assignment.IsActive) assignment.IsActive = true;

            var firstEverLesson = !await db.Lessons.AnyAsync(x => x.StudentId == offer.StudentId);
            var lesson = new Lesson
            {
                TeacherId = teacherId,
                StudentId = offer.StudentId,
                StartsAt = offer.StartsAt,
                EndsAt = offer.EndsAt,
                IsTrial = firstEverLesson,
            };
            db.Lessons.Add(lesson);
            offer.LessonId = lesson.Id;

            // The student is booked now, so their other offers must not stay claimable.
            await db.OpenStudentOffers.Where(x => x.StudentId == offer.StudentId && x.Status == "open" && x.Id != offer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "withdrawn"));

            var studentName = offer.Student?.FullName ?? "A student";
            var teacherName = await db.Users.Where(x => x.Id == teacherId).Select(x => x.FullName).FirstOrDefaultAsync() ?? "A teacher";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                offer.StudentId,
                "A teacher accepted your time",
                $"{teacherName} booked the time you offered. Open your schedule to see the lesson.",
                "booking",
                "/schedule",
                $"open-offer-claimed:{offer.Id}"));

            var broadcastKey = $"open-offer-broadcast:{offer.Id}";
            await db.Notifications.Where(x => x.DedupeKey == broadcastKey && x.UserId == teacherId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Title, "You booked this time")
                    .SetProperty(x => x.Body, $"{studentName}'s offered time is now on your schedule."));
            await db.Notifications.Where(x => x.DedupeKey == broadcastKey && x.UserId != teacherId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Title, "Time offer taken")
                    .SetProperty(x => x.Body, $"{studentName} chose {teacherName}. Their offered time is no longer open.")
                    // Re-surface the notice: the outcome is new information even
                    // for a teacher who already read the original offer.
                    .SetProperty(x => x.ReadAt, (DateTimeOffset?)null));

            try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync();
                return Results.Conflict(new { message = "This time was just accepted by another teacher." });
            }

            var teacherIds = await ActiveTeacherIdsAsync(db);
            await realtime.ToParticipantsAsync(teacherId, offer.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUsersAsync(teacherIds.Append(offer.StudentId), RealtimeScopes.Offers);
            await realtime.ToUsersAsync(teacherIds.Append(offer.StudentId), RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Ok(new { lessonId = lesson.Id, offer = ToOpenOffer(offer, studentName, teacherName) });
        });

        api.MapPost("/student/lesson-requests/{id:guid}/confirm", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var request = await db.LessonRequests.FirstOrDefaultAsync(x => x.Id == id && x.StudentId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_student") return Results.Conflict(new { message = "This request is not awaiting your confirmation." });
            request.Status = "pending_admin";
            db.Notifications.Add(new AppNotification
            {
                UserId = request.TeacherId,
                Title = "Student confirmed your proposed time",
                Body = "The lesson is now waiting for academy approval.",
                Kind = "request",
                Link = "/schedule",
                DedupeKey = $"request-student-accepted:{request.Id}",
            });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(request.TeacherId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Ok(await RequestById(db, id));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/student/lesson-requests/{id:guid}/reject", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var request = await db.LessonRequests.Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id && x.StudentId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_student") return Results.Conflict(new { message = "This request is not awaiting your confirmation." });
            request.Status = "rejected"; request.StudentAvailability!.Status = "available";
            db.Notifications.Add(new AppNotification
            {
                UserId = request.TeacherId,
                Title = "Student declined the proposed time",
                Body = "The time is open again. Choose another preferred time from the student.",
                Kind = "request",
                Link = "/schedule",
                DedupeKey = $"request-student-declined:{request.Id}",
            });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUserAsync(request.TeacherId, RealtimeScopes.Notifications);
            await realtime.ToStaffAsync(RealtimeScopes.Admin);
            return Results.Ok(await RequestById(db, id));
        }).RequireAuthorization("StudentOnly");

        api.MapGet("/lessons", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            if (HasRole(p, "student")) await EnsureRollingLessonsAsync(db, db.LessonSeries.Where(x => x.StudentId == UserId(p)));
            else if (HasRole(p, "teacher")) await EnsureRollingLessonsAsync(db, db.LessonSeries.Where(x => x.TeacherId == UserId(p)));
            var q = db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review).AsQueryable();
            if (HasRole(p, "student")) q = q.Where(x => x.StudentId == UserId(p));
            else if (HasRole(p, "teacher")) q = q.Where(x => x.TeacherId == UserId(p));
            else if (!IsAdmin(p)) return Results.Forbid();
            return Results.Ok((await q.OrderBy(x => x.StartsAt).ToListAsync()).Select(ToLesson));
        }).RequireAuthorization();
        api.MapGet("/lessons/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var l = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            // Allow-list: admins and the two participants only. Support reviews
            // lessons through the dedicated reviewer endpoints, which never expose
            // the student's private report/review feedback.
            if (!IsAdmin(p) && l.StudentId != UserId(p) && l.TeacherId != UserId(p)) return Results.Forbid();
            return Results.Ok(ToLesson(l));
        }).RequireAuthorization();

        // Capture is server-side (Jigasi + Whisper); the bot authenticates with a
        // shared key because it has no user session. Disabled until the key is set.
        api.MapPost("/lessons/{id:guid}/transcript", async (Guid id, HttpContext http, TranscriptUploadRequest input, AcademyDbContext db, IConfiguration config) =>
        {
            var key = config["Transcripts:IngestKey"];
            if (string.IsNullOrWhiteSpace(key)) return Results.NotFound();
            if (!SecretsMatch(http.Request.Headers["X-Transcript-Key"].ToString(), key)) return Results.Unauthorized();
            var incoming = input.Segments ?? [];
            if (incoming.Count == 0) return Results.BadRequest(new { message = "No segments supplied." });
            if (incoming.Count > 200) return Results.BadRequest(new { message = "Send at most 200 segments per request." });
            if (!await db.Lessons.AsNoTracking().AnyAsync(x => x.Id == id)) return Results.NotFound();
            // Bound the stored transcript per lesson so a replayed or leaked
            // ingest key cannot grow the table without limit.
            var stored = await db.LessonTranscriptSegments.CountAsync(x => x.LessonId == id);
            if (stored >= MaxTranscriptSegmentsPerLesson)
                return Results.Conflict(new { message = "This lesson already has the maximum number of transcript segments." });

            var now = DateTimeOffset.UtcNow;
            var accepted = 0;
            var flagged = 0;
            foreach (var segment in incoming)
            {
                var text = segment.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.Length > 4000) text = text[..4000];
                var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "Unknown" : segment.Speaker.Trim();
                if (speaker.Length > 120) speaker = speaker[..120];
                var flags = TranscriptFlags.Scan(text);
                if (flags.Count > 0) flagged++;
                db.LessonTranscriptSegments.Add(new LessonTranscriptSegment
                {
                    LessonId = id,
                    Speaker = speaker,
                    Text = text,
                    AtSeconds = segment.AtSeconds is >= 0 ? segment.AtSeconds : null,
                    Flags = string.Join(",", flags),
                    CreatedAt = now
                });
                accepted++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { accepted, flagged });
        }).AllowAnonymous().RequireRateLimiting("transcripts");

        api.MapPatch("/lessons/{id:guid}", async (Guid id, UpdateLessonRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            // Direct time edits are an academy override. Teachers and students
            // move lessons through the accept/decline reschedule workflow.
            if (!IsAdmin(p)) return Results.Forbid();
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Series).Include(x => x.Report).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (input.Status == "cancelled")
                return Results.Conflict(new { message = "Use the cancellation endpoint so the policy and notifications are applied." });
            if (input.Status == "completed")
                return Results.Conflict(new { message = "The teacher completes a lesson by submitting its report." });
            var startsAt = input.StartsAt ?? lesson.StartsAt;
            var endsAt = input.EndsAt ?? lesson.EndsAt;
            if (endsAt <= startsAt) return Results.BadRequest(new { message = "The lesson must end after it starts." });
            if (input.Status is not null && input.Status is not ("scheduled" or "in_progress")) return Results.BadRequest(new { message = "Unknown lesson status." });
            var conflict = await db.Lessons.AnyAsync(x => x.Id != id && x.Status != "cancelled" && x.StartsAt < endsAt && x.EndsAt > startsAt && (x.TeacherId == lesson.TeacherId || x.StudentId == lesson.StudentId));
            if (conflict) return Results.Conflict(new { message = "A teacher or student already has a lesson at this time." });
            var moved = startsAt != lesson.StartsAt || endsAt != lesson.EndsAt;
            var previousStarts = lesson.StartsAt;
            if (moved)
            {
                db.LessonScheduleChanges.Add(new LessonScheduleChange
                {
                    LessonId = lesson.Id,
                    FromStartsAt = lesson.StartsAt,
                    FromEndsAt = lesson.EndsAt,
                    ToStartsAt = startsAt,
                    ToEndsAt = endsAt,
                    ChangedById = UserId(p),
                    Reason = "Academy schedule override",
                });
                var pending = await db.LessonRescheduleRequests
                    .Where(x => x.LessonId == id && x.Status == "pending").ToListAsync();
                foreach (var item in pending) item.Status = "expired";
                var movedTo = LessonOpsMessaging.When(startsAt, lesson.Series?.TimeZoneId);
                db.Notifications.AddRange(
                    new AppNotification { UserId = lesson.TeacherId, Title = "Lesson time updated", Body = $"The academy moved the lesson to {movedTo}.", Kind = "lesson", Link = $"/lesson/{lesson.Id}", DedupeKey = $"lesson-time:{lesson.Id}:{startsAt:O}" },
                    new AppNotification { UserId = lesson.StudentId, Title = "Lesson time updated", Body = $"The academy moved the lesson to {movedTo}.", Kind = "lesson", Link = $"/lesson/{lesson.Id}", DedupeKey = $"lesson-time:{lesson.Id}:{startsAt:O}" });
            }
            lesson.StartsAt = startsAt; lesson.EndsAt = endsAt;
            if (input.Status is not null) lesson.Status = input.Status;
            db.AuditLogs.Add(new AuditLog
            {
                ActorId = UserId(p),
                Action = "lesson_updated",
                EntityType = "Lesson",
                EntityId = lesson.Id,
                Detail = moved ? $"Academy moved lesson from {previousStarts:u}." : "Academy updated lesson status.",
            });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            if (moved) await realtime.ToUsersAsync([lesson.TeacherId, lesson.StudentId], RealtimeScopes.Notifications);
            return Results.Ok(ToLesson(lesson));
        }).RequireAuthorization("AdminOnly");
        api.MapDelete("/lessons/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, AcademyEmailSender email, AcademyPushSender push, RealtimeNotifier realtime, CancellationToken ct) =>
            await LessonOperationsEndpoints.CancelAsync(id, null, p, db, email, push, realtime, ct)).RequireAuthorization();

        api.MapGet("/lessons/{id:guid}/report", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (!IsAdmin(p) && !(HasRole(p, "teacher") && lesson.TeacherId == UserId(p)) && !(HasRole(p, "student") && lesson.StudentId == UserId(p))) return Results.Forbid();
            return lesson.Report is null ? Results.NotFound() : Results.Ok(ToReport(lesson.Report, lesson));
        }).RequireAuthorization();

        api.MapPost("/lessons/{id:guid}/report", async (Guid id, SaveLessonReportRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var teacherId = UserId(p);
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
            if (lesson is null) return Results.NotFound();
            if (lesson.Status == "cancelled") return Results.Conflict(new { message = "A cancelled lesson cannot receive a class report." });
            if (DateTimeOffset.UtcNow < lesson.EndsAt) return Results.Conflict(new { message = "You can submit the class report after the lesson ends." });

            var summary = (input.ClassSummary ?? "").Trim();
            var feedback = (input.StudentFeedback ?? "").Trim();
            var progress = (input.Progress ?? "good").Trim().ToLowerInvariant();
            var errors = new Dictionary<string, string[]>();
            if (summary.Length is < 10 or > 2000) errors["classSummary"] = ["Summarize the class in 10 to 2000 characters."];
            if (feedback.Length is < 10 or > 2000) errors["studentFeedback"] = ["Give feedback about the student in 10 to 2000 characters."];
            if (progress is not ("excellent" or "good" or "needs_attention")) errors["progress"] = ["Choose excellent, good, or needs_attention."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var report = lesson.Report;
            var firstReport = report is null;
            if (report is null)
            {
                report = new LessonReport { LessonId = lesson.Id, TeacherId = lesson.TeacherId, StudentId = lesson.StudentId };
                db.LessonReports.Add(report);
            }
            report.ClassSummary = summary;
            report.StudentFeedback = feedback;
            report.Progress = progress;
            report.UpdatedAt = DateTimeOffset.UtcNow;
            if (lesson.Status != "completed")
            {
                lesson.Status = "completed";
                lesson.ActualEndedAt ??= lesson.EndsAt;
            }
            // Submitting the report completes the lesson, so this is the point
            // where attendance is closed and payable time becomes reviewable.
            var policy = await LessonPolicyService.GetPolicyAsync(db);
            await LessonPolicyService.CloseAttendanceAsync(db, lesson, DateTimeOffset.UtcNow, policy);
            await LessonPolicyService.EnsureTeacherHoursAsync(db, lesson, policy, DateTimeOffset.UtcNow);
            if (firstReport)
                db.Notifications.Add(new AppNotification
                {
                    UserId = lesson.StudentId,
                    Title = "New lesson report",
                    Body = $"{lesson.Teacher?.FullName ?? "Your teacher"} shared feedback from your lesson.",
                    Kind = "report",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"report-student:{lesson.Id}",
                });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            await realtime.ToUserAsync(lesson.TeacherId, RealtimeScopes.Billing);
            if (firstReport) await realtime.ToUserAsync(lesson.StudentId, RealtimeScopes.Notifications);
            return Results.Ok(ToReport(report, lesson));
        }).RequireAuthorization("TeacherOnly");

        api.MapGet("/lessons/{id:guid}/review", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (!IsAdmin(p) && !(HasRole(p, "teacher") && lesson.TeacherId == UserId(p)) && !(HasRole(p, "student") && lesson.StudentId == UserId(p))) return Results.Forbid();
            return lesson.Review is null ? Results.NotFound() : Results.Ok(ToReview(lesson.Review, lesson));
        }).RequireAuthorization();

        api.MapPost("/lessons/{id:guid}/review", async (Guid id, SaveTeacherReviewRequest input, ClaimsPrincipal p, AcademyDbContext db, RealtimeNotifier realtime) =>
        {
            var studentId = UserId(p);
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id && x.StudentId == studentId);
            if (lesson is null) return Results.NotFound();
            if (lesson.Status == "cancelled") return Results.Conflict(new { message = "A cancelled lesson cannot be reviewed." });
            if (DateTimeOffset.UtcNow < lesson.EndsAt) return Results.Conflict(new { message = "You can review the teacher after the lesson ends." });
            if (input.Rating is < 1 or > 5)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["Choose a rating from 1 to 5 stars."] });
            var comment = (input.Comment ?? "").Trim();
            if (comment.Length > 2000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["comment"] = ["Keep the review under 2000 characters."] });

            var review = lesson.Review;
            var firstReview = review is null;
            if (review is null)
            {
                review = new TeacherReview { LessonId = lesson.Id, TeacherId = lesson.TeacherId, StudentId = lesson.StudentId };
                db.TeacherReviews.Add(review);
            }
            review.Rating = input.Rating;
            review.Comment = comment.Length == 0 ? null : comment;
            review.UpdatedAt = DateTimeOffset.UtcNow;
            if (firstReview)
                db.Notifications.Add(new AppNotification
                {
                    UserId = lesson.TeacherId,
                    Title = "New student review",
                    Body = $"{PublicStudentName(lesson.Student?.FullName)} rated your lesson {input.Rating} out of 5.",
                    Kind = "lesson",
                    Link = $"/lesson/{lesson.Id}",
                    DedupeKey = $"review-teacher:{lesson.Id}",
                });
            await db.SaveChangesAsync();
            await realtime.ToParticipantsAsync(lesson.TeacherId, lesson.StudentId, RealtimeScopes.Lessons, lesson.Id);
            if (firstReview) await realtime.ToUserAsync(lesson.TeacherId, RealtimeScopes.Notifications);
            return Results.Ok(ToReview(review, lesson));
        }).RequireAuthorization("StudentOnly");

        api.MapGet("/profile/reports", async (int? skip, int? take, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var q = db.Lessons.AsNoTracking()
                .Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report)
                .Where(x => x.Report != null);
            if (HasRole(p, "student")) q = q.Where(x => x.StudentId == UserId(p));
            else if (HasRole(p, "teacher")) q = q.Where(x => x.TeacherId == UserId(p));
            else if (!IsAdmin(p)) return Results.Forbid();

            var offset = Math.Max(skip ?? 0, 0);
            var pageSize = Math.Clamp(take ?? 20, 1, 50);
            // Fetch one extra row so the client learns about the next page
            // without a second COUNT query.
            var lessons = await q
                .OrderByDescending(x => x.StartsAt).ThenByDescending(x => x.Id)
                .Skip(offset).Take(pageSize + 1)
                .ToListAsync();
            var hasMore = lessons.Count > pageSize;
            if (hasMore) lessons.RemoveAt(pageSize);
            return Results.Ok(new { items = lessons.Select(x => ToReport(x.Report!, x)).ToList(), hasMore });
        }).RequireAuthorization();
    }

    private static async Task<IResult> ApproveRequest(Guid id, AcademyDbContext db, RealtimeNotifier realtime)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var request = await db.LessonRequests.Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id);
        if (request is null) return Results.NotFound();
        if (request.Status != "pending_admin") return Results.Conflict(new { message = "This request is not awaiting admin approval." });
        var slot = request.AvailabilitySlot;
        var studentSlot = request.StudentAvailability;
        var startsAt = slot?.StartsAt ?? studentSlot!.StartsAt;
        var endsAt = slot?.EndsAt ?? studentSlot!.EndsAt;
        var conflict = await db.Lessons.AnyAsync(x => x.Status == "scheduled" && x.StartsAt < endsAt && x.EndsAt > startsAt && (x.TeacherId == request.TeacherId || x.StudentId == request.StudentId));
        if (conflict) return Results.Conflict(new { message = "A teacher or student already has a lesson at this time." });
        var claimed = slot is not null
            ? await db.AvailabilitySlots.Where(x => x.Id == slot.Id && x.Status == "requested").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "booked"))
            : await db.StudentAvailabilities.Where(x => x.Id == studentSlot!.Id && x.Status == "requested").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "booked"));
        if (claimed == 0) return Results.Conflict(new { message = "The availability slot is no longer held by this request." });
        request.Status = "approved"; request.AdminRespondedAt = DateTimeOffset.UtcNow;
        db.Lessons.Add(new Lesson { TeacherId = request.TeacherId, StudentId = request.StudentId, LessonRequestId = request.Id, StartsAt = startsAt, EndsAt = endsAt });
        var people = await db.Users.Where(x => x.Id == request.TeacherId || x.Id == request.StudentId)
            .Select(x => new { x.Id, x.FullName }).ToListAsync();
        var teacherName = people.FirstOrDefault(x => x.Id == request.TeacherId)?.FullName ?? "your teacher";
        var studentName = people.FirstOrDefault(x => x.Id == request.StudentId)?.FullName ?? "your student";
        db.Notifications.AddRange(
            new AppNotification
            {
                UserId = request.StudentId,
                Title = "Lesson confirmed",
                Body = $"Your lesson with {teacherName} is confirmed. Open your schedule for the time.",
                Kind = "lesson",
                Link = "/schedule",
                DedupeKey = $"request-approved:{request.Id}:student",
            },
            new AppNotification
            {
                UserId = request.TeacherId,
                Title = "New lesson confirmed",
                Body = $"{studentName} is confirmed. Open your schedule for the time.",
                Kind = "lesson",
                Link = "/schedule",
                DedupeKey = $"request-approved:{request.Id}:teacher",
            });
        try
        {
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync();
            return Results.Conflict(new { message = "This lesson conflicts with another booking or was approved by another admin action. Refresh and try again." });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync();
            return Results.Conflict(new { message = "This lesson conflicts with another booking or was approved by another admin action. Refresh and try again." });
        }
        await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
        await realtime.ToUsersAsync([request.TeacherId, request.StudentId], RealtimeScopes.Notifications);
        await realtime.ToStaffAsync(RealtimeScopes.Admin);
        return Results.Ok(await RequestById(db, id));
    }

    private static async Task<IResult> RejectRequestByAdmin(Guid id, string? reason, AcademyDbContext db, RealtimeNotifier realtime)
    {
        var request = await db.LessonRequests.Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id);
        if (request is null) return Results.NotFound(); if (request.Status != "pending_admin") return Results.Conflict(new { message = "This request is not awaiting admin approval." });
        request.Status = "rejected"; request.RejectionReason = reason; request.AdminRespondedAt = DateTimeOffset.UtcNow;
        if (request.AvailabilitySlot is not null) request.AvailabilitySlot.Status = "available";
        if (request.StudentAvailability is not null) request.StudentAvailability.Status = "available";
        db.Notifications.AddRange(
            new AppNotification
            {
                UserId = request.StudentId,
                Title = "Lesson request could not be confirmed",
                Body = reason ?? "The academy could not confirm this time. Book another weekly slot or message us.",
                Kind = "request",
                Link = "/book",
                DedupeKey = $"request-rejected:{request.Id}:student",
            },
            new AppNotification
            {
                UserId = request.TeacherId,
                Title = "Lesson request could not be confirmed",
                Body = reason ?? "The academy could not confirm this lesson time.",
                Kind = "request",
                Link = "/schedule",
                DedupeKey = $"request-rejected:{request.Id}:teacher",
            });
        await db.SaveChangesAsync();
        await realtime.ToParticipantsAsync(request.TeacherId, request.StudentId, RealtimeScopes.Lessons);
        await realtime.ToUsersAsync([request.TeacherId, request.StudentId], RealtimeScopes.Notifications);
        await realtime.ToStaffAsync(RealtimeScopes.Admin);
        return Results.Ok(await RequestById(db, id));
    }

    private static WeeklyRuleDto ToRule(TeacherAvailabilityRule r, string status) => new(r.Id, r.TeacherId, r.DayOfWeek, FormatTime(r.StartsAtTime), FormatTime(r.EndsAtTime), r.TimeZoneId, status);
    private static OpenStudentOfferDto ToOpenOffer(OpenStudentOffer offer, string studentName, string? claimedByTeacherName)
        => new(offer.Id, offer.StudentId, studentName, offer.StartsAt, offer.EndsAt, offer.Status, offer.ClaimedByTeacherId, claimedByTeacherName, offer.LessonId, offer.CreatedAt, offer.ClaimedAt);
    // Lessons are standardized so billing, reminders, and reports stay predictable.
    private static readonly TimeSpan[] AllowedLessonLengths = [TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45), TimeSpan.FromHours(1)];
    private const string LessonLengthMessage = "A lesson can last 30, 45, or 60 minutes.";
    private static bool HasAllowedLessonLength(TimeSpan duration) => AllowedLessonLengths.Contains(duration);
    private static async Task<List<Guid>> ActiveTeacherIdsAsync(AcademyDbContext db, CancellationToken ct = default)
    {
        var roleId = await db.Roles.Where(r => r.NormalizedName == "TEACHER").Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (roleId == Guid.Empty) return [];
        return await db.UserRoles.Where(x => x.RoleId == roleId)
            .Join(db.Users.Where(u => u.IsActive), x => x.UserId, u => u.Id, (x, u) => u.Id)
            .Distinct().ToListAsync(ct);
    }
    private static LessonSeriesDto ToSeries(LessonSeries s) => new(s.Id, s.TeacherId, DisplayName(s.Teacher?.FullName) ?? "Teacher", s.StudentId, DisplayName(s.Student?.FullName) ?? "Student", s.DayOfWeek, FormatTime(s.StartsAtTime), FormatTime(s.EndsAtTime), s.TimeZoneId, s.IsActive, s.CreatedAt);
    private static LessonChangeRequestDto ToChangeRequest(LessonChangeRequest r) => new(
        r.Id, r.SeriesId, r.TeacherId, DisplayName(r.Teacher?.FullName) ?? "Teacher", r.StudentId, DisplayName(r.Student?.FullName) ?? "Student",
        r.Series?.DayOfWeek ?? 0, FormatTime(r.Series?.StartsAtTime ?? TimeOnly.MinValue), FormatTime(r.Series?.EndsAtTime ?? TimeOnly.MinValue),
        r.RequestedRule?.DayOfWeek ?? 0, FormatTime(r.RequestedRule?.StartsAtTime ?? TimeOnly.MinValue), FormatTime(r.RequestedRule?.EndsAtTime ?? TimeOnly.MinValue),
        r.RequestedRule?.TimeZoneId ?? "UTC", r.Status, r.StudentNote, r.AdminNote, r.CreatedAt, r.AdminRespondedAt);
    private static IQueryable<LessonChangeRequest> ChangeRequestsWithDetails(AcademyDbContext db)
        => db.LessonChangeRequests.Include(x => x.Series).Include(x => x.RequestedRule).Include(x => x.Teacher).Include(x => x.Student);

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static bool Overlaps(int dayA, TimeOnly startA, TimeOnly endA, int dayB, TimeOnly startB, TimeOnly endB)
        => dayA == dayB && startA < endB && endA > startB;
    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        => TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;
    private static DateTimeOffset InstantFor(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
    private static IEnumerable<(DateTimeOffset StartsAt, DateTimeOffset EndsAt)> NextOccurrences(
        int dayOfWeek, TimeOnly startsAtTime, TimeOnly endsAtTime, TimeZoneInfo zone, DateTimeOffset now, int weeks)
    {
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var i = 0; i <= weeks * 7; i++)
        {
            var date = localToday.AddDays(i);
            if ((int)date.DayOfWeek != dayOfWeek) continue;
            var start = InstantFor(date, startsAtTime, zone);
            if (start <= now) continue;
            yield return (start, InstantFor(date, endsAtTime, zone));
        }
    }

    private static async Task<List<WeeklyRuleDto>> WeeklyRulesAsync(AcademyDbContext db, Guid teacherId, Guid? studentId)
    {
        var rules = await db.TeacherAvailabilityRules.Where(x => x.TeacherId == teacherId)
            .OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartsAtTime).ToListAsync();
        var bookings = await db.LessonSeries.Where(x => x.IsActive && x.TeacherId == teacherId)
            .Select(x => new { x.AvailabilityRuleId, x.StudentId }).ToListAsync();
        return rules.Select(rule =>
        {
            var booking = bookings.FirstOrDefault(x => x.AvailabilityRuleId == rule.Id);
            var status = booking is null ? "available" : booking.StudentId == studentId ? "yours" : "booked";
            return ToRule(rule, status);
        }).ToList();
    }

    private static async Task<WeeklyAvailabilityDto> WeeklyAvailabilityAsync(AcademyDbContext db, AcademyUser teacher, Guid studentId)
    {
        var rules = await WeeklyRulesAsync(db, teacher.Id, studentId);
        var series = await db.LessonSeries.Include(x => x.Teacher).Include(x => x.Student)
            .Where(x => x.StudentId == studentId && x.TeacherId == teacher.Id && x.IsActive)
            .OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartsAtTime).ToListAsync();
        var rating = await TeacherRatingAsync(db, teacher.Id);
        var reviews = await db.TeacherReviews.AsNoTracking().Include(x => x.Lesson).Include(x => x.Teacher).Include(x => x.Student)
            .Where(x => x.TeacherId == teacher.Id).OrderByDescending(x => x.CreatedAt).Take(5).ToListAsync();
        var hasLessons = await db.Lessons.AnyAsync(x => x.StudentId == studentId);
        return new WeeklyAvailabilityDto(ToPerson(teacher, "teacher"), rules, series.Select(ToSeries).ToList(), rating.Average, rating.Count, reviews.Select(ToReview).ToList(), hasLessons);
    }

    private static async Task<(List<TeacherAvailabilityRule>? Created, string? Error)> CreateAvailabilityRulesAsync(
        Guid teacherId, CreateAvailabilityRulesRequest input, AcademyDbContext db)
    {
        var days = (input.DaysOfWeek ?? []).Distinct().OrderBy(x => x).ToList();
        if (days.Count == 0 || days.Any(x => x is < 0 or > 6)) return (null, "Choose at least one weekday.");
        if (!TimeOnly.TryParse(input.StartsAtTime, CultureInfo.InvariantCulture, out var starts)) return (null, "Enter a valid start time.");
        if (!TimeOnly.TryParse(input.EndsAtTime, CultureInfo.InvariantCulture, out var ends)) return (null, "Enter a valid end time.");
        starts = new TimeOnly(starts.Hour, starts.Minute);
        ends = new TimeOnly(ends.Hour, ends.Minute);
        if (ends <= starts) return (null, "The end time must be after the start time.");
        if (!HasAllowedLessonLength(ends - starts)) return (null, LessonLengthMessage);
        var timeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? "UTC" : input.TimeZoneId.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _)) return (null, "Unknown time zone.");

        var existing = await db.TeacherAvailabilityRules.Where(x => x.TeacherId == teacherId).ToListAsync();
        var created = new List<TeacherAvailabilityRule>();
        foreach (var day in days)
        {
            if (existing.Concat(created).Any(x => x.DayOfWeek == day && starts < x.EndsAtTime && ends > x.StartsAtTime))
                return (null, "Those hours overlap availability you already announced.");
            created.Add(new TeacherAvailabilityRule
            {
                TeacherId = teacherId,
                DayOfWeek = day,
                StartsAtTime = starts,
                EndsAtTime = ends,
                TimeZoneId = timeZoneId,
            });
        }
        db.TeacherAvailabilityRules.AddRange(created);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return (null, "Those hours overlap availability you already announced."); }
        return (created, null);
    }

    private static async Task<IResult> DeleteAvailabilityRuleAsync(Guid teacherId, Guid ruleId, AcademyDbContext db)
    {
        var rule = await db.TeacherAvailabilityRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.TeacherId == teacherId);
        if (rule is null) return Results.NotFound();
        if (await db.LessonSeries.AnyAsync(x => x.AvailabilityRuleId == ruleId && x.IsActive))
            return Results.Conflict(new { message = "Students are still booked on this weekly time. End their lessons first." });
        if (await db.LessonChangeRequests.AnyAsync(x => x.RequestedRuleId == ruleId))
            return Results.Conflict(new { message = "This slot is recorded in a weekly time change, so it cannot be removed." });
        db.TeacherAvailabilityRules.Remove(rule);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// Ends every active weekly series built on one announced slot, so an
    /// administrator can take a time back from the students who booked it. The
    /// release mirrors the student's own "end weekly lessons": future lessons
    /// were never charged, so no credit moves and nobody takes a strike.
    /// </summary>
    private static async Task<IResult> ReleaseAvailabilityRuleAsync(Guid teacherId, Guid ruleId, Guid actorId, AcademyDbContext db, RealtimeNotifier realtime)
    {
        if (!await db.TeacherAvailabilityRules.AnyAsync(x => x.Id == ruleId && x.TeacherId == teacherId))
            return Results.NotFound();
        var seriesList = await db.LessonSeries.Include(x => x.Student).Include(x => x.Teacher)
            .Where(x => x.AvailabilityRuleId == ruleId && x.IsActive).ToListAsync();
        if (seriesList.Count == 0) return Results.Ok(new { released = 0, lessonsCancelled = 0 });

        var now = DateTimeOffset.UtcNow;
        var cancelled = 0;
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var series in seriesList)
        {
            series.IsActive = false;
            var futureLessons = await db.Lessons
                .Where(x => x.SeriesId == series.Id && x.Status == "scheduled" && x.StartsAt > now).ToListAsync();
            foreach (var lesson in futureLessons)
            {
                lesson.Status = "cancelled";
                lesson.CancellationOutcome = "series_ended";
                lesson.CancelReason = "The academy ended this weekly time.";
                lesson.CancelledById = actorId;
                lesson.CancelledAt = now;
                cancelled++;
            }

            var keepsTeacher = await db.LessonSeries.AnyAsync(x =>
                x.StudentId == series.StudentId && x.TeacherId == series.TeacherId && x.IsActive && x.Id != series.Id);
            if (!keepsTeacher)
                await db.Assignments.Where(x => x.StudentId == series.StudentId && x.TeacherId == series.TeacherId && x.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));

            var when = LessonOpsMessaging.WeeklyAt(series.DayOfWeek, series.StartsAtTime, series.TimeZoneId);
            var studentName = series.Student?.FullName ?? "A student";
            var teacherName = series.Teacher?.FullName ?? "their teacher";
            db.Notifications.Add(LessonOpsMessaging.Notice(
                series.TeacherId, "Weekly lessons ended",
                $"The academy ended {studentName}'s weekly lessons with you on {when}. Their future lessons are off your calendar.",
                "booking", "/schedule", $"series-ended-by-admin:{series.Id}"));
            db.Notifications.Add(LessonOpsMessaging.Notice(
                series.StudentId, "Weekly lessons ended",
                $"The academy ended your weekly lessons with {teacherName} on {when}. You can book another time.",
                "booking", "/book", $"series-ended-by-admin:{series.Id}"));
        }
        Audit(db, actorId, "admin_availability_released", "TeacherAvailabilityRule", ruleId, $"seriesEnded={seriesList.Count}; lessonsCancelled={cancelled}");

        try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
        catch (DbUpdateException) { await tx.RollbackAsync(); return Results.Conflict(new { message = "Unable to end these weekly lessons. Please try again." }); }

        foreach (var series in seriesList)
        {
            await realtime.ToParticipantsAsync(series.TeacherId, series.StudentId, RealtimeScopes.Lessons);
            await realtime.ToUsersAsync(new[] { series.TeacherId, series.StudentId }, RealtimeScopes.Notifications);
        }
        await realtime.ToUserAsync(teacherId, RealtimeScopes.Directory);
        await realtime.ToStaffAsync(RealtimeScopes.Directory);
        await realtime.ToStaffAsync(RealtimeScopes.Admin);
        return Results.Ok(new { released = seriesList.Count, lessonsCancelled = cancelled });
    }

    private static async Task<List<Lesson>> BuildSeriesLessonsAsync(AcademyDbContext db, LessonSeries series, DateTimeOffset now)
    {
        var zone = ResolveTimeZone(series.TimeZoneId);
        var existing = await db.Lessons.Where(x => x.SeriesId == series.Id && x.StartsAt >= now.AddDays(-1))
            .Select(x => x.StartsAt).ToListAsync();
        var lessons = new List<Lesson>();
        foreach (var (start, end) in NextOccurrences(series.DayOfWeek, series.StartsAtTime, series.EndsAtTime, zone, now, 8))
        {
            if (existing.Any(x => Math.Abs((x - start).TotalMinutes) < 1)) continue;
            if (await db.Lessons.AnyAsync(x => x.Status == "scheduled" && x.StartsAt < end && x.EndsAt > start
                && (x.TeacherId == series.TeacherId || x.StudentId == series.StudentId))) continue;
            lessons.Add(new Lesson
            {
                TeacherId = series.TeacherId,
                StudentId = series.StudentId,
                SeriesId = series.Id,
                StartsAt = start,
                EndsAt = end,
            });
        }
        db.Lessons.AddRange(lessons);
        return lessons;
    }

    /// <summary>
    /// A series has no end date, so lessons are materialized on a rolling
    /// window. Extend when fewer than four weeks of lessons remain.
    /// </summary>
    private static async Task EnsureRollingLessonsAsync(AcademyDbContext db, IQueryable<LessonSeries> query)
    {
        var series = await query.Where(x => x.IsActive).ToListAsync();
        if (series.Count == 0) return;
        var ids = series.Select(x => x.Id).ToList();
        var latest = await db.Lessons.Where(x => x.SeriesId != null && ids.Contains(x.SeriesId.Value))
            .GroupBy(x => x.SeriesId!.Value)
            .Select(g => new { Id = g.Key, Max = g.Max(x => x.StartsAt) })
            .ToDictionaryAsync(x => x.Id, x => x.Max);
        var now = DateTimeOffset.UtcNow;
        var toExtend = series.Where(s => !latest.TryGetValue(s.Id, out var max) || max < now.AddDays(28)).ToList();
        if (toExtend.Count == 0) return;
        foreach (var s in toExtend) await BuildSeriesLessonsAsync(db, s, now);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    /// <summary>
    /// The checks a weekly-time move must pass before anything is written. The
    /// student only ever picks a slot the teacher already published, so there is
    /// nothing to approve; what remains is making sure the slot is still free.
    /// </summary>
    private static async Task<string?> CheckTimeChangeAsync(
        LessonSeries series, TeacherAvailabilityRule rule, AcademyDbContext db, DateTimeOffset now)
    {
        if (!series.IsActive) return "This booking is no longer active.";
        if (await db.LessonSeries.AnyAsync(x => x.IsActive && x.AvailabilityRuleId == rule.Id && x.Id != series.Id))
            return "That time is no longer available.";
        var otherSeries = await db.LessonSeries
            .Where(x => x.StudentId == series.StudentId && x.IsActive && x.Id != series.Id).ToListAsync();
        if (otherSeries.Any(x => Overlaps(x.DayOfWeek, x.StartsAtTime, x.EndsAtTime, rule.DayOfWeek, rule.StartsAtTime, rule.EndsAtTime)))
            return "That time overlaps another lesson you have.";
        foreach (var (start, end) in NextOccurrences(rule.DayOfWeek, rule.StartsAtTime, rule.EndsAtTime, ResolveTimeZone(rule.TimeZoneId), now, 8))
            if (await db.Lessons.AnyAsync(x => x.Status == "scheduled" && x.SeriesId != series.Id && x.StartsAt < end && x.EndsAt > start
                && (x.TeacherId == series.TeacherId || x.StudentId == series.StudentId)))
                return "The requested time conflicts with an existing lesson.";
        return null;
    }

    /// <summary>
    /// Applies a weekly-time move and returns the reason it could not be
    /// applied, or null once it is written. The checks run inside a serializable
    /// transaction because two students choosing the same free slot at the same
    /// moment must not both win.
    /// </summary>
    private static async Task<string?> RecordTimeChangeAsync(
        AcademyDbContext db, LessonSeries series, TeacherAvailabilityRule rule,
        LessonChangeRequest request, DateTimeOffset now, AcademyPushSender? push = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await CheckTimeChangeAsync(series, rule, db, now) is { } blocked)
        {
            await tx.RollbackAsync();
            return blocked;
        }
        if (db.Entry(request).State == EntityState.Detached) db.LessonChangeRequests.Add(request);
        request.Status = "applied";
        var notices = new List<AppNotification>();
        string? conflict = null;
        try
        {
            await db.Lessons.Where(x => x.SeriesId == series.Id && x.StartsAt > now).ExecuteDeleteAsync();
            series.AvailabilityRuleId = rule.Id;
            series.DayOfWeek = rule.DayOfWeek;
            series.StartsAtTime = rule.StartsAtTime;
            series.EndsAtTime = rule.EndsAtTime;
            series.TimeZoneId = rule.TimeZoneId;
            notices = await AddTimeChangeNoticesAsync(db, series, request);
            await db.SaveChangesAsync();
            await BuildSeriesLessonsAsync(db, series, now);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or PostgresException)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            conflict = "That time was just taken. Pick another slot.";
        }
        if (conflict is not null) return conflict;

        // Only announce a move that committed, and only to the pair whose week
        // changed; the academy's copy stays a feed item nobody has to be woken for.
        if (push is not null)
            foreach (var notice in notices.Where(x => x.UserId == series.StudentId || x.UserId == series.TeacherId))
                await LessonOpsMessaging.PushAsync(db, push, [notice.UserId], notice.Title, notice.Body, notice.Link);
        return null;
    }

    /// <summary>
    /// The student and the teacher get the new time. The academy is copied for
    /// the record only: it no longer decides anything, but a weekly time moving
    /// under its roof is worth seeing. Returns every row written, so the caller can
    /// announce the ones that matter once the change is committed.
    /// </summary>
    private static async Task<List<AppNotification>> AddTimeChangeNoticesAsync(
        AcademyDbContext db, LessonSeries series, LessonChangeRequest request)
    {
        var studentName = series.Student?.FullName ?? "A student";
        var teacherName = series.Teacher?.FullName ?? "your teacher";
        var newAt = LessonOpsMessaging.WeeklyAt(series.DayOfWeek, series.StartsAtTime, series.TimeZoneId);
        var notices = new List<AppNotification>
        {
            LessonOpsMessaging.Notice(series.StudentId, "Weekly time changed",
                $"Your weekly time with {teacherName} moved to {newAt}.",
                "request", "/schedule", $"change-applied:{request.Id}:student"),
            LessonOpsMessaging.Notice(series.TeacherId, "Weekly time changed",
                $"{studentName}'s weekly time moved to {newAt}.",
                "request", "/schedule", $"change-applied:{request.Id}:teacher")
        };
        db.Notifications.AddRange(notices);
        foreach (var adminId in await LessonOpsMessaging.AdminUserIdsAsync(db))
        {
            var copy = LessonOpsMessaging.Notice(adminId, "Weekly time changed",
                $"{studentName} moved their weekly time with {teacherName} to {newAt}.",
                "request", "/requests", $"change-applied:{request.Id}:admin:{adminId}");
            db.Notifications.Add(copy);
            notices.Add(copy);
        }
        return notices;
    }

    /// <summary>
    /// Weekly time changes stopped needing an admin decision, so anything still
    /// sitting in the old approval queue is applied here — or closed with the
    /// reason it no longer works, so the student can simply ask again.
    /// </summary>
    internal static async Task ApplyPendingTimeChangesAsync(AcademyDbContext db)
    {
        var ids = await db.LessonChangeRequests.Where(x => x.Status == "pending")
            .OrderBy(x => x.CreatedAt).Select(x => x.Id).ToListAsync();
        if (ids.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids)
        {
            var request = await db.LessonChangeRequests
                .Include(x => x.Series).ThenInclude(x => x!.Teacher)
                .Include(x => x.Series).ThenInclude(x => x!.Student)
                .Include(x => x.RequestedRule)
                .FirstOrDefaultAsync(x => x.Id == id && x.Status == "pending");
            if (request?.Series is null) continue;
            var rule = request.RequestedRule is not null && request.RequestedRule.TeacherId == request.TeacherId
                ? request.RequestedRule
                : null;
            var error = rule is null
                ? "That time is no longer published by this teacher."
                : await RecordTimeChangeAsync(db, request.Series, rule, request, now);
            if (error is null) continue;

            // A rejected write clears the tracker, so reload the row before closing it.
            var open = await db.LessonChangeRequests.FirstOrDefaultAsync(x => x.Id == id && x.Status == "pending");
            if (open is null) continue;
            open.Status = "rejected";
            open.AdminNote = error;
            open.AdminRespondedAt = now;
            db.Notifications.Add(LessonOpsMessaging.Notice(open.StudentId, "Weekly time change closed",
                "Weekly times now move as soon as you ask, so this older request was closed. Ask again to move to a new time.",
                "request", "/schedule", $"change-closed:{open.Id}"));
            await db.SaveChangesAsync();
        }
    }

    private static async Task<LessonRequestDto?> RequestById(AcademyDbContext db, Guid id)
    {
        var r = await db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id);
        return r is null ? null : ToRequest(r);
    }
    private static async Task<IReadOnlyList<AssignmentDto>> AssignmentList(AcademyDbContext db)
        => await db.Assignments.Include(x => x.Teacher).Include(x => x.Student).Where(x => x.IsActive).OrderBy(x => x.CreatedAt).Select(x => new AssignmentDto(x.Id, x.TeacherId, x.Teacher!.FullName, x.StudentId, x.Student!.FullName, x.CreatedAt, x.IsActive, x.IsActive ? "active" : "inactive")).ToListAsync();
    private static async Task<AssignmentDto?> AssignmentById(AcademyDbContext db, Guid id)
        => await db.Assignments.Include(x => x.Teacher).Include(x => x.Student).Where(x => x.Id == id).Select(x => new AssignmentDto(x.Id, x.TeacherId, x.Teacher!.FullName, x.StudentId, x.Student!.FullName, x.CreatedAt, x.IsActive, x.IsActive ? "active" : "inactive")).FirstOrDefaultAsync();

    private const long MaxAvatarBytes = 2 * 1024 * 1024;
    // Generous ceiling for a transcripted lesson; bounds what a leaked ingest key
    // can store per lesson regardless of how many requests it makes.
    private const int MaxTranscriptSegmentsPerLesson = 5000;

    private static bool SecretsMatch(string provided, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool LooksLikeImage(byte[] data, string contentType) => contentType switch
    {
        "image/png" => data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47,
        "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
        "image/webp" => data.Length >= 12
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P',
        _ => false,
    };
}
