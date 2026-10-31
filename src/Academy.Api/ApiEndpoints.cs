using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Data;
using Npgsql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public static class ApiEndpoints
{
    private static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static bool HasRole(ClaimsPrincipal p, string role) => p.IsInRole(role);
    private static bool IsAdmin(ClaimsPrincipal p) => p.IsInRole("admin") || p.IsInRole("super_admin");
    private static UserDto ToUser(AcademyUser u, string role) => new(u.Id, u.FullName, u.Email ?? "", role.ToLowerInvariant(), u.IsActive, u.FullName, u.IsActive, u.FullName, u.PhoneNumber, $"/api/users/{u.Id}/avatar");
    private static async Task<string> RoleOf(UserManager<AcademyUser> users, AcademyUser user)
        => (await users.GetRolesAsync(user)).FirstOrDefault() ?? "student";
    private static LessonRequestDto ToRequest(LessonRequest r)
    {
        var startsAt = r.AvailabilitySlot?.StartsAt ?? r.StudentAvailability!.StartsAt;
        var endsAt = r.AvailabilitySlot?.EndsAt ?? r.StudentAvailability!.EndsAt;
        return new(r.Id, r.TeacherId, r.Teacher?.FullName ?? "", r.StudentId, r.Student?.FullName ?? "", r.AvailabilitySlotId, r.StudentAvailabilityId, r.AvailabilitySlotId.HasValue ? "student" : "teacher", startsAt, endsAt, r.Status, r.CreatedAt, r.RejectionReason);
    }
    /// <summary>
    /// Identity reports a duplicate sign-in name and a duplicate email as two
    /// separate errors. Here both are the same value, so they would surface twice;
    /// collapse them into one message.
    /// </summary>
    private static Dictionary<string, string[]> IdentityErrors(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            return new() { ["email"] = ["An account with this email already exists."] };
        return result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray());
    }
    private static string PublicStudentName(string? fullName)
    {
        var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "Student",
            1 => parts[0],
            _ => $"{parts[0]} {parts[^1][0]}."
        };
    }
    private static LessonReportDto ToReport(LessonReport r, Lesson l) => new(r.Id, l.Id, l.StartsAt, l.EndsAt, l.TeacherId, l.Teacher?.FullName ?? "", l.StudentId, l.Student?.FullName ?? "", r.ClassSummary, r.StudentFeedback, r.Progress, r.CreatedAt, r.UpdatedAt);
    private static TeacherReviewDto ToReview(TeacherReview r, Lesson l) => new(r.Id, l.Id, l.StartsAt, r.TeacherId, r.Teacher?.FullName ?? l.Teacher?.FullName ?? "", r.StudentId, PublicStudentName(r.Student?.FullName ?? l.Student?.FullName), r.Rating, r.Comment, r.CreatedAt, r.UpdatedAt);
    private static TeacherReviewDto ToReview(TeacherReview r) => r.Lesson is null
        ? new(r.Id, r.LessonId, default, r.TeacherId, r.Teacher?.FullName ?? "", r.StudentId, PublicStudentName(r.Student?.FullName), r.Rating, r.Comment, r.CreatedAt, r.UpdatedAt)
        : ToReview(r, r.Lesson);
    private static LessonDto ToLesson(Lesson l) => new(l.Id, l.TeacherId, l.Teacher?.FullName ?? "", l.StudentId, l.Student?.FullName ?? "", l.StartsAt, l.EndsAt, l.Status, l.ActualStartedAt, l.ActualEndedAt, l.Report is null ? null : ToReport(l.Report, l), l.Review is null ? null : ToReview(l.Review, l));
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
        auth.MapPost("/register", async (RegisterRequest input, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["registration"] = ["Full name, valid email and a password of at least 8 characters are required."] });
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), EmailConfirmed = true };
            var result = await users.CreateAsync(user, input.Password);
            if (!result.Succeeded) return Results.ValidationProblem(IdentityErrors(result));
            await users.AddToRoleAsync(user, "student");
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Created("/api/auth/me", new { user = ToUser(user, "student") });
        });
        auth.MapPost("/login", async (LoginRequest input, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password)) return Results.Problem("Invalid email or password.", statusCode: 401);
            var user = await users.FindByEmailAsync(input.Email.Trim());
            if (user is null || !user.IsActive) return Results.Problem("Invalid email or password.", statusCode: 401);
            var check = await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true);
            if (!check.Succeeded) return Results.Problem("Invalid email or password.", statusCode: 401);
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
        api.MapPost("/auth/change-password", async (ChangePasswordRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn) =>
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
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");

        auth.MapPost("/password/request", async (PasswordResetRequestInput input, PasswordResetStore resets, AcademyEmailSender email, UserManager<AcademyUser> users, HttpContext http) =>
        {
            var address = input.Email?.Trim() ?? "";
            if (!System.Net.Mail.MailAddress.TryCreate(address, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });
            if (!resets.DevMode && !email.IsConfigured)
                return Results.Problem("Password reset email is not available right now. Please contact the academy.", statusCode: 503);

            var key = address.ToLowerInvariant();
            var user = await users.FindByEmailAsync(address);
            // The response is identical whether or not the account exists.
            if (user is null || !user.IsActive)
                return Results.Accepted(value: new { message = "If an account uses this email, a reset code has been sent." });

            var code = resets.TryIssue(key);
            if (code is null) return Results.Problem("Please wait a minute before requesting another code.", statusCode: 429);
            if (!resets.DevMode)
            {
                var sent = await email.SendPasswordResetCodeAsync(user.Email!, code, 15, http.RequestAborted);
                if (!sent)
                {
                    resets.Consume(key);
                    return Results.Problem("Could not send the reset email. Please try again.", statusCode: 502);
                }
            }
            return Results.Accepted(value: new
            {
                message = "If an account uses this email, a reset code has been sent.",
                devCode = resets.DevMode ? code : null,
            });
        }).RequireRateLimiting("otp");

        auth.MapPost("/password/reset", async (PasswordResetInput input, PasswordResetStore resets, UserManager<AcademyUser> users) =>
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
            resets.Consume(key);
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

        profile.MapPatch("", async (UpdateProfileRequest input, ClaimsPrincipal principal, UserManager<AcademyUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            var errors = new Dictionary<string, string[]>();
            var fullName = input.FullName?.Trim();
            var email = input.Email?.Trim();
            string? phone = null;

            if (input.FullName is not null && (fullName!.Length is < 2 or > 160))
                errors["fullName"] = ["Enter your full name (2 to 160 characters)."];
            if (input.Email is not null && (email!.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(email, out _)))
                errors["email"] = ["Enter a valid email address."];
            if (input.PhoneNumber is not null)
            {
                phone = PhoneNumbers.Normalize(input.PhoneNumber);
                if (phone is null) errors["phoneNumber"] = ["Enter a valid mobile number in international format."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (email is not null && !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                var emailOwner = await users.FindByEmailAsync(email);
                if (emailOwner is not null && emailOwner.Id != user.Id)
                    return Results.Conflict(new { message = "An account with this email already exists." });
                var nameOwner = await users.FindByNameAsync(email);
                if (nameOwner is not null && nameOwner.Id != user.Id)
                    return Results.Conflict(new { message = "That email is already in use." });
                user.Email = email;
                user.UserName = email;
                user.EmailConfirmed = true;
            }
            if (phone is not null && !string.Equals(phone, user.PhoneNumber, StringComparison.Ordinal))
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
            return Results.Ok(new { user = ToUser(user, await RoleOf(users, user)) });
        });

        profile.MapPost("/avatar", async (HttpContext http, ClaimsPrincipal principal, AcademyDbContext db, UserManager<AcademyUser> users) =>
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
            return Results.Ok(new { avatarUrl = $"/api/users/{user.Id}/avatar" });
        });

        profile.MapDelete("/avatar", async (ClaimsPrincipal principal, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var avatar = await db.UserAvatars.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (avatar is not null)
            {
                db.UserAvatars.Remove(avatar);
                await db.SaveChangesAsync();
            }
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

        auth.MapPost("/otp/verify", async (OtpVerifyRequest input, PhoneOtpStore otp, UserManager<AcademyUser> users, SignInManager<AcademyUser> signIn) =>
        {
            var phone = PhoneNumbers.Normalize(input.Phone);
            if (phone is null || string.IsNullOrWhiteSpace(input.Code))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["otp"] = ["Enter the phone number and the code you received."] });
            if (!otp.Check(phone, input.Code.Trim())) return Results.Problem("The code is invalid or has expired.", statusCode: 401);

            var user = users.Users.FirstOrDefault(x => x.PhoneNumber == phone);
            if (user is null)
            {
                var fullName = input.FullName?.Trim() ?? "";
                if (fullName.Length == 0) return Results.Ok(new { requiresProfile = true });
                if (fullName.Length is < 2 or > 160)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["fullName"] = ["Enter your full name (2 to 160 characters)."] });
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

        api.MapPost("/contact", async (ContactRequest input, AcademyDbContext db, AcademyEmailSender notifier, HttpContext http) =>
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
            // The receiver is fixed by server configuration, never by the request body.
            await notifier.SendContactMessageAsync(submission, http.RequestAborted);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("contact");

        var admin = api.MapGroup("/admin").RequireAuthorization("AdminOnly");
        admin.MapPost("/users", async (CreateUserRequest input, UserManager<AcademyUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password) || string.IsNullOrWhiteSpace(input.Role)) return Results.BadRequest(new { message = "Full name, email, password and role are required." });
            var role = input.Role.Trim().ToLowerInvariant();
            if (role is not ("teacher" or "admin")) return Results.BadRequest(new { message = "Admin can create teacher or admin accounts only." });
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), EmailConfirmed = true };
            var created = await users.CreateAsync(user, input.Password);
            if (!created.Succeeded) return Results.ValidationProblem(IdentityErrors(created));
            await users.AddToRoleAsync(user, role);
            return Results.Created($"/api/{role}s/{user.Id}", new { user = ToUser(user, role) });
        });
        admin.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserRequest input, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var role = await RoleOf(users, user);
            if (role is not ("teacher" or "student")) return Results.BadRequest(new { message = "Only teacher and student accounts can be edited here." });
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
            }
            if (input.IsActive == false) await db.Assignments.Where(x => (x.TeacherId == id || x.StudentId == id) && x.IsActive).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            return Results.Ok(ToUser(user, role));
        });
        // Deletes are soft: lessons keep their historical teacher/student owners.
        admin.MapDelete("/users/{id:guid}", async (Guid id, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var role = await RoleOf(users, user);
            if (role is not ("teacher" or "student")) return Results.BadRequest(new { message = "Only teacher and student accounts can be deactivated here." });
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
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded) return Results.ValidationProblem(updated.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            await db.SaveChangesAsync();
            await db.Assignments.Where(x => (x.TeacherId == id || x.StudentId == id) && x.IsActive).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            await db.AvailabilitySlots.Where(x => x.TeacherId == id && x.Status == "available" && x.StartsAt > now).ExecuteDeleteAsync();
            return Results.NoContent();
        });
        admin.MapGet("/teachers/{teacherId:guid}/availability", async (Guid teacherId, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var teacher = await users.FindByIdAsync(teacherId.ToString());
            if (teacher is null || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            return Results.Ok(await db.AvailabilitySlots.Where(x => x.TeacherId == teacherId && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync());
        });
        admin.MapPost("/teachers/{teacherId:guid}/availability", async (Guid teacherId, PublishAvailabilityRequest input, UserManager<AcademyUser> users, AcademyDbContext db) =>
        {
            var teacher = await users.FindByIdAsync(teacherId.ToString());
            if (teacher is null || !teacher.IsActive || !await users.IsInRoleAsync(teacher, "teacher")) return Results.NotFound();
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt || input.EndsAt - input.StartsAt > TimeSpan.FromHours(4)) return Results.BadRequest(new { message = "Choose a future slot with a duration up to 4 hours." });
            var conflict = await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == teacherId && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.Lessons.AnyAsync(x => x.TeacherId == teacherId && x.Status == "scheduled" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (conflict) return Results.Conflict(new { message = "This time overlaps another published slot or lesson." });
            var slot = new AvailabilitySlot { TeacherId = teacherId, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.AvailabilitySlots.Add(slot); await db.SaveChangesAsync();
            return Results.Created($"/api/admin/teachers/{teacherId}/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        admin.MapDelete("/teachers/{teacherId:guid}/availability/{slotId:guid}", async (Guid teacherId, Guid slotId, AcademyDbContext db) =>
        {
            var slot = await db.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId);
            if (slot is null) return Results.NotFound();
            if (slot.Status != "available") return Results.Conflict(new { message = "A requested or booked slot cannot be deleted." });
            db.AvailabilitySlots.Remove(slot); await db.SaveChangesAsync(); return Results.NoContent();
        });
        admin.MapGet("/contact-submissions", async (AcademyDbContext db) => await db.ContactSubmissions.OrderByDescending(x => x.CreatedAt).ToListAsync());
        admin.MapGet("/lesson-requests", async (string? status, AcademyDbContext db) =>
        {
            var q = db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            return Results.Ok((await q.OrderBy(x => x.CreatedAt).ToListAsync()).Select(ToRequest));
        });
        admin.MapPost("/lesson-requests/{id:guid}/approve", async (Guid id, AcademyDbContext db) => await ApproveRequest(id, db));
        admin.MapPost("/lesson-requests/{id:guid}/reject", async (Guid id, AcademyDbContext db, RejectRequest? input = null) => await RejectRequestByAdmin(id, input?.Reason, db));

        admin.MapGet("/assignments", async (AcademyDbContext db) => await AssignmentList(db));
        api.MapGet("/teacher-student-assignments", async (AcademyDbContext db) => await AssignmentList(db)).RequireAuthorization("AdminOnly");
        api.MapPost("/teacher-student-assignments", async (AssignStudentRequest input, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var teacher = await users.FindByIdAsync(input.TeacherId.ToString());
            var student = await users.FindByIdAsync(input.StudentId.ToString());
            if (teacher is null || student is null || !await users.IsInRoleAsync(teacher, "teacher") || !await users.IsInRoleAsync(student, "student")) return Results.BadRequest(new { message = "A valid teacher and student are required." });
            var activeElsewhere = await db.Assignments.Where(x => x.StudentId == input.StudentId && x.IsActive && x.TeacherId != input.TeacherId).ToListAsync();
            foreach (var previous in activeElsewhere) previous.IsActive = false;
            var existing = await db.Assignments.FirstOrDefaultAsync(x => x.TeacherId == input.TeacherId && x.StudentId == input.StudentId);
            if (existing is not null) { existing.IsActive = true; await db.SaveChangesAsync(); return Results.Ok(await AssignmentById(db, existing.Id)); }
            var assignment = new TeacherStudentAssignment { TeacherId = input.TeacherId, StudentId = input.StudentId };
            db.Assignments.Add(assignment); await db.SaveChangesAsync();
            return Results.Created($"/api/teacher-student-assignments/{assignment.Id}", await AssignmentById(db, assignment.Id));
        }).RequireAuthorization("AdminOnly");
        api.MapDelete("/teacher-student-assignments/{id:guid}", async (Guid id, AcademyDbContext db) =>
        {
            var item = await db.Assignments.FindAsync(id); if (item is null) return Results.NotFound();
            item.IsActive = false; await db.SaveChangesAsync(); return Results.NoContent();
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
                        ToUser(teacher, "teacher"),
                        slots.Where(slot => slot.TeacherId == teacher.Id)
                            .Select(slot => new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status))
                            .ToList(),
                        rating?.Average,
                        rating?.Count ?? 0);
                })
                .ToList();
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
            return Results.Ok(new TeacherAvailabilityDto(ToUser(teacher, "teacher"), slots, average, reviewCount, reviews.Select(ToReview).ToList()));
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
                    teacher.FullName,
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
            return Results.Ok(new PublicTeacherDto(teacher.Id, teacher.FullName, $"/api/users/{teacher.Id}/avatar", average, reviewCount, reviews.Select(ToReview).ToList()));
        }).AllowAnonymous();
        api.MapGet("/students", async (bool? includeInactive, ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            if (!IsAdmin(p) && !HasRole(p, "teacher")) return Results.Forbid();
            var includeInactiveForAdmin = includeInactive == true && IsAdmin(p);
            var q = users.Users.Where(x => x.IsActive || includeInactiveForAdmin);
            if (HasRole(p, "teacher")) { var id = UserId(p); var studentIds = db.Assignments.Where(a => a.TeacherId == id && a.IsActive).Select(a => a.StudentId); q = q.Where(u => studentIds.Contains(u.Id)); }
            var list = await q.ToListAsync(); var result = new List<UserDto>();
            foreach (var user in list) if (await users.IsInRoleAsync(user, "student")) result.Add(ToUser(user, "student"));
            return Results.Ok(result);
        }).RequireAuthorization();
        api.MapPost("/students", async (CreateStudentRequest input, UserManager<AcademyUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || !System.Net.Mail.MailAddress.TryCreate(input.Email, out _) || string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["student"] = ["Full name, a valid email and a password of at least 8 characters are required."] });
            if (await users.FindByEmailAsync(input.Email.Trim()) is not null) return Results.Conflict(new { message = "An account with this email already exists." });
            var user = new AcademyUser { Id = Guid.NewGuid(), UserName = input.Email.Trim(), Email = input.Email.Trim(), FullName = input.FullName.Trim(), EmailConfirmed = true };
            var created = await users.CreateAsync(user, input.Password);
            if (!created.Succeeded) return Results.ValidationProblem(IdentityErrors(created));
            await users.AddToRoleAsync(user, "student");
            return Results.Created($"/api/students/{user.Id}", ToUser(user, "student"));
        }).RequireAuthorization("AdminOnly");
        api.MapGet("/students/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, UserManager<AcademyUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString()); if (user is null || !await users.IsInRoleAsync(user, "student")) return Results.NotFound();
            if (HasRole(p, "teacher") && !await db.Assignments.AnyAsync(a => a.TeacherId == UserId(p) && a.StudentId == id && a.IsActive)) return Results.Forbid();
            if (!IsAdmin(p) && !HasRole(p, "teacher") && UserId(p) != id) return Results.Forbid();
            return Results.Ok(ToUser(user, "student"));
        }).RequireAuthorization();

        var teacher = api.MapGroup("/teacher").RequireAuthorization("TeacherOnly");
        teacher.MapGet("/availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.AvailabilitySlots.Where(x => x.TeacherId == UserId(p) && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync()));
        teacher.MapPost("/availability", async (PublishAvailabilityRequest input, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt || input.EndsAt - input.StartsAt > TimeSpan.FromHours(4)) return Results.BadRequest(new { message = "Choose a future slot with a duration up to 4 hours." });
            var id = UserId(p);
            var conflict = await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == id && x.Status != "available" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.Lessons.AnyAsync(x => x.TeacherId == id && x.Status == "scheduled" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt)
                || await db.AvailabilitySlots.AnyAsync(x => x.TeacherId == id && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (conflict) return Results.Conflict(new { message = "This time overlaps another published slot or lesson." });
            var slot = new AvailabilitySlot { TeacherId = id, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.AvailabilitySlots.Add(slot); await db.SaveChangesAsync();
            return Results.Created($"/api/teacher/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        teacher.MapDelete("/availability/{slotId:guid}", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var slot = await db.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == slotId && x.TeacherId == UserId(p));
            if (slot is null) return Results.NotFound(); if (slot.Status != "available") return Results.Conflict(new { message = "A requested or booked slot cannot be deleted." });
            db.AvailabilitySlots.Remove(slot); await db.SaveChangesAsync(); return Results.NoContent();
        });
        teacher.MapGet("/lesson-requests", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok((await db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).Where(x => x.TeacherId == UserId(p)).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToRequest)));
        teacher.MapPost("/lesson-requests/{id:guid}/confirm", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var request = await db.LessonRequests.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_teacher") return Results.Conflict(new { message = "This request is no longer awaiting teacher confirmation." });
            request.Status = "pending_admin"; request.TeacherRespondedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            return Results.Ok(await RequestById(db, id));
        });
        teacher.MapPost("/lesson-requests/{id:guid}/reject", async (Guid id, ClaimsPrincipal p, AcademyDbContext db, RejectRequest? input = null) =>
        {
            var request = await db.LessonRequests.Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_teacher") return Results.Conflict(new { message = "This request is no longer awaiting teacher confirmation." });
            request.Status = "rejected"; request.RejectionReason = input?.Reason; request.TeacherRespondedAt = DateTimeOffset.UtcNow; request.AvailabilitySlot!.Status = "available";
            await db.SaveChangesAsync(); return Results.Ok(await RequestById(db, id));
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
            return Results.Ok(new TeacherAvailabilityDto(ToUser(assignment.Teacher, "teacher"), slots, rating.Average, rating.Count, reviews.Select(x => ToReview(x)).ToList()));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/lesson-requests", async (RequestSlotInput input, ClaimsPrincipal p, AcademyDbContext db) =>
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
            try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
            catch (DbUpdateException) { await tx.RollbackAsync(); await db.AvailabilitySlots.Where(x => x.Id == slot.Id && x.Status == "requested").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "available")); return Results.Conflict(new { message = "A request is already active or the slot was claimed." }); }
            return Results.Created($"/api/lesson-requests/{request.Id}", await RequestById(db, request.Id));
        }).RequireAuthorization("StudentOnly");
        api.MapGet("/student/lesson-requests", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok((await db.LessonRequests.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).Where(x => x.StudentId == UserId(p)).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToRequest))).RequireAuthorization("StudentOnly");

        var studentApi = api.MapGroup("/student").RequireAuthorization("StudentOnly");
        studentApi.MapGet("/availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.StudentAvailabilities.Where(x => x.StudentId == UserId(p) && x.StartsAt > DateTimeOffset.UtcNow.AddDays(-1)).OrderBy(x => x.StartsAt).Select(x => new AvailabilityDto(x.Id, x.TeacherId, x.StartsAt, x.EndsAt, x.Status)).ToListAsync()));
        studentApi.MapPost("/availability", async (PublishAvailabilityRequest input, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var studentId = UserId(p);
            if (input.StartsAt <= DateTimeOffset.UtcNow || input.EndsAt <= input.StartsAt || input.EndsAt - input.StartsAt > TimeSpan.FromHours(4)) return Results.BadRequest(new { message = "Choose a future preferred time with a duration up to 4 hours." });
            var assignment = await db.Assignments.Where(x => x.StudentId == studentId && x.IsActive).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            if (assignment is null) return Results.Conflict(new { message = "You do not have an assigned teacher yet." });
            var overlaps = await db.StudentAvailabilities.AnyAsync(x => x.StudentId == studentId && x.Status != "rejected" && x.StartsAt < input.EndsAt && x.EndsAt > input.StartsAt);
            if (overlaps) return Results.Conflict(new { message = "This overlaps another preferred time you have submitted." });
            var slot = new StudentAvailability { StudentId = studentId, TeacherId = assignment.TeacherId, StartsAt = input.StartsAt.ToUniversalTime(), EndsAt = input.EndsAt.ToUniversalTime() };
            db.StudentAvailabilities.Add(slot); await db.SaveChangesAsync();
            return Results.Created($"/api/student/availability/{slot.Id}", new AvailabilityDto(slot.Id, slot.TeacherId, slot.StartsAt, slot.EndsAt, slot.Status));
        });
        studentApi.MapDelete("/availability/{slotId:guid}", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var slot = await db.StudentAvailabilities.FirstOrDefaultAsync(x => x.Id == slotId && x.StudentId == UserId(p));
            if (slot is null) return Results.NotFound(); if (slot.Status != "available") return Results.Conflict(new { message = "A selected or booked preference cannot be deleted." });
            db.StudentAvailabilities.Remove(slot); await db.SaveChangesAsync(); return Results.NoContent();
        });
        api.MapGet("/teacher/student-availability", async (ClaimsPrincipal p, AcademyDbContext db) => Results.Ok(await db.StudentAvailabilities.Include(x => x.Student).Where(x => x.TeacherId == UserId(p) && x.Status == "available" && x.StartsAt > DateTimeOffset.UtcNow).OrderBy(x => x.StartsAt).Select(x => new { x.Id, x.StudentId, studentName = x.Student!.FullName, x.StartsAt, x.EndsAt, x.Status }).ToListAsync())).RequireAuthorization("TeacherOnly");
        api.MapPost("/teacher/student-availability/{slotId:guid}/select", async (Guid slotId, ClaimsPrincipal p, AcademyDbContext db) =>
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
            return Results.Created($"/api/lesson-requests/{request.Id}", await RequestById(db, request.Id));
        }).RequireAuthorization("TeacherOnly");
        api.MapPost("/student/lesson-requests/{id:guid}/confirm", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var request = await db.LessonRequests.FirstOrDefaultAsync(x => x.Id == id && x.StudentId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_student") return Results.Conflict(new { message = "This request is not awaiting your confirmation." });
            request.Status = "pending_admin"; await db.SaveChangesAsync(); return Results.Ok(await RequestById(db, id));
        }).RequireAuthorization("StudentOnly");
        api.MapPost("/student/lesson-requests/{id:guid}/reject", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var request = await db.LessonRequests.Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id && x.StudentId == UserId(p));
            if (request is null) return Results.NotFound(); if (request.Status != "pending_student") return Results.Conflict(new { message = "This request is not awaiting your confirmation." });
            request.Status = "rejected"; request.StudentAvailability!.Status = "available"; await db.SaveChangesAsync(); return Results.Ok(await RequestById(db, id));
        }).RequireAuthorization("StudentOnly");

        api.MapGet("/lessons", async (ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var q = db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review).AsQueryable();
            if (HasRole(p, "student")) q = q.Where(x => x.StudentId == UserId(p));
            else if (HasRole(p, "teacher")) q = q.Where(x => x.TeacherId == UserId(p));
            return Results.Ok((await q.OrderBy(x => x.StartsAt).ToListAsync()).Select(ToLesson));
        }).RequireAuthorization();
        api.MapGet("/lessons/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var l = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound(); if (HasRole(p, "student") && l.StudentId != UserId(p) || HasRole(p, "teacher") && l.TeacherId != UserId(p)) return Results.Forbid();
            return Results.Ok(ToLesson(l));
        }).RequireAuthorization();
        api.MapPatch("/lessons/{id:guid}", async (Guid id, UpdateLessonRequest input, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (HasRole(p, "teacher") && lesson.TeacherId != UserId(p) || HasRole(p, "student") && lesson.StudentId != UserId(p)) return Results.Forbid();
            var startsAt = input.StartsAt ?? lesson.StartsAt;
            var endsAt = input.EndsAt ?? lesson.EndsAt;
            if (endsAt <= startsAt) return Results.BadRequest(new { message = "The lesson must end after it starts." });
            if (input.Status is not null && input.Status is not ("scheduled" or "in_progress" or "completed" or "cancelled")) return Results.BadRequest(new { message = "Unknown lesson status." });
            var conflict = await db.Lessons.AnyAsync(x => x.Id != id && x.Status == "scheduled" && x.StartsAt < endsAt && x.EndsAt > startsAt && (x.TeacherId == lesson.TeacherId || x.StudentId == lesson.StudentId));
            if (conflict) return Results.Conflict(new { message = "A teacher or student already has a lesson at this time." });
            lesson.StartsAt = startsAt; lesson.EndsAt = endsAt;
            if (input.Status is not null) lesson.Status = input.Status;
            await db.SaveChangesAsync();
            return Results.Ok(ToLesson(lesson));
        }).RequireAuthorization();
        api.MapDelete("/lessons/{id:guid}", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (HasRole(p, "teacher") && lesson.TeacherId != UserId(p) || HasRole(p, "student") && lesson.StudentId != UserId(p)) return Results.Forbid();
            if (lesson.Status != "cancelled") { lesson.Status = "cancelled"; await db.SaveChangesAsync(); }
            return Results.NoContent();
        }).RequireAuthorization();

        api.MapGet("/lessons/{id:guid}/report", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Report).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (!IsAdmin(p) && !(HasRole(p, "teacher") && lesson.TeacherId == UserId(p)) && !(HasRole(p, "student") && lesson.StudentId == UserId(p))) return Results.Forbid();
            return lesson.Report is null ? Results.NotFound() : Results.Ok(ToReport(lesson.Report, lesson));
        }).RequireAuthorization();

        api.MapPost("/lessons/{id:guid}/report", async (Guid id, SaveLessonReportRequest input, ClaimsPrincipal p, AcademyDbContext db) =>
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
            await db.SaveChangesAsync();
            return Results.Ok(ToReport(report, lesson));
        }).RequireAuthorization("TeacherOnly");

        api.MapGet("/lessons/{id:guid}/review", async (Guid id, ClaimsPrincipal p, AcademyDbContext db) =>
        {
            var lesson = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student).Include(x => x.Review).FirstOrDefaultAsync(x => x.Id == id);
            if (lesson is null) return Results.NotFound();
            if (!IsAdmin(p) && !(HasRole(p, "teacher") && lesson.TeacherId == UserId(p)) && !(HasRole(p, "student") && lesson.StudentId == UserId(p))) return Results.Forbid();
            return lesson.Review is null ? Results.NotFound() : Results.Ok(ToReview(lesson.Review, lesson));
        }).RequireAuthorization();

        api.MapPost("/lessons/{id:guid}/review", async (Guid id, SaveTeacherReviewRequest input, ClaimsPrincipal p, AcademyDbContext db) =>
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
            if (review is null)
            {
                review = new TeacherReview { LessonId = lesson.Id, TeacherId = lesson.TeacherId, StudentId = lesson.StudentId };
                db.TeacherReviews.Add(review);
            }
            review.Rating = input.Rating;
            review.Comment = comment.Length == 0 ? null : comment;
            review.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
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

    private static async Task<IResult> ApproveRequest(Guid id, AcademyDbContext db)
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
        return Results.Ok(await RequestById(db, id));
    }

    private static async Task<IResult> RejectRequestByAdmin(Guid id, string? reason, AcademyDbContext db)
    {
        var request = await db.LessonRequests.Include(x => x.AvailabilitySlot).Include(x => x.StudentAvailability).FirstOrDefaultAsync(x => x.Id == id);
        if (request is null) return Results.NotFound(); if (request.Status != "pending_admin") return Results.Conflict(new { message = "This request is not awaiting admin approval." });
        request.Status = "rejected"; request.RejectionReason = reason; request.AdminRespondedAt = DateTimeOffset.UtcNow;
        if (request.AvailabilitySlot is not null) request.AvailabilitySlot.Status = "available";
        if (request.StudentAvailability is not null) request.StudentAvailability.Status = "available";
        await db.SaveChangesAsync(); return Results.Ok(await RequestById(db, id));
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
