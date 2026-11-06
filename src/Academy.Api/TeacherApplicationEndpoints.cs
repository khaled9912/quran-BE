using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Academy.Api;

/// <summary>
/// Public teacher applications: anonymous submission with a CV, an optional
/// self-introduction recording, and a required Quran recitation, plus an admin
/// queue that can approve (create the teacher account and send a set-password
/// code) or reject with a reason.
/// </summary>
public static class TeacherApplicationEndpoints
{
    private const long MaxCvBytes = 2 * 1024 * 1024;
    private const long MaxRecordingBytes = 20 * 1024 * 1024;
    private const long MaxFormBytes = MaxCvBytes + MaxRecordingBytes * 2 + 1024 * 1024;

    private static readonly string[] AllowedSubjects = ["quran", "tajweed", "islamic_studies", "arabic"];
    private static readonly string[] AllowedEnglishLevels = ["beginner", "intermediate", "advanced", "fluent"];
    private static readonly string[] AllowedIntroContentTypes =
    [
        "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/wav", "audio/x-wav",
        "audio/ogg", "audio/webm", "video/mp4", "video/webm", "video/quicktime",
    ];

    public static void MapTeacherApplications(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/teacher-applications", SubmitApplicationAsync)
            .AllowAnonymous()
            .RequireRateLimiting("applications");

        var admin = api.MapGroup("/admin").RequireAuthorization("AdminOnly");
        admin.MapGet("/teacher-applications", ListApplicationsAsync);
        admin.MapGet("/teacher-applications/{id:guid}/cv", DownloadCvAsync);
        admin.MapGet("/teacher-applications/{id:guid}/intro", DownloadIntroAsync);
        admin.MapGet("/teacher-applications/{id:guid}/quran", DownloadQuranRecordingAsync);
        admin.MapPatch("/teacher-applications/{id:guid}", ReviewApplicationAsync);
    }

    private static async Task<IResult> SubmitApplicationAsync(
        HttpContext http,
        [FromServices] AcademyDbContext db,
        [FromServices] UserManager<AcademyUser> users)
    {
        if (!http.Request.HasFormContentType)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["application"] = ["Submit the application form with your CV attached."] });

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(
                new FormOptions { MultipartBodyLengthLimit = MaxFormBytes },
                http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Problem("The uploaded files are too large. Keep the CV under 2 MB and each recording under 20 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var errors = new Dictionary<string, string[]>();
        var fullName = form["fullName"].ToString().Trim();
        var email = form["email"].ToString().Trim();
        var phone = PhoneNumbers.Normalize(form["phoneNumber"].ToString());
        var englishLevel = form["englishLevel"].ToString().Trim().ToLowerInvariant();
        var bio = form["bio"].ToString().Trim();
        var availabilityNote = form["availabilityNote"].ToString().Trim();

        if (fullName.Length is < 2 or > 160)
            errors["fullName"] = ["Enter your full name (2 to 160 characters)."];
        else if (ApiEndpoints.NameContainsContact(fullName))
            errors["fullName"] = ["Enter your name, not an email address."];
        if (email.Length is 0 or > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            errors["email"] = ["Enter a valid email address."];
        if (phone is null)
            errors["phoneNumber"] = ["Enter a valid mobile number in international format."];

        var subjects = form["subjects"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (subjects.Count == 0 || subjects.Any(subject => !AllowedSubjects.Contains(subject)))
            errors["subjects"] = ["Choose at least one subject: Quran, Tajweed, Islamic studies, or Arabic."];

        if (!AllowedEnglishLevels.Contains(englishLevel))
            errors["englishLevel"] = ["Choose your English level: Beginner, Intermediate, Advanced, or Fluent."];

        if (!int.TryParse(form["experienceYears"].ToString(), out var experienceYears) || experienceYears is < 0 or > 70)
            errors["experienceYears"] = ["Enter your years of teaching experience (0 to 70)."];
        if (bio.Length is < 20 or > 2000)
            errors["bio"] = ["Write a short introduction (20 to 2000 characters)."];
        if (availabilityNote.Length > 500)
            errors["availabilityNote"] = ["Keep the availability note under 500 characters."];

        var file = form.Files.GetFile("cv");
        string? fileName = null;
        string? contentType = null;
        byte[]? fileData = null;
        if (file is null || file.Length == 0)
        {
            errors["cv"] = ["Attach your CV (PDF, DOC, or DOCX, up to 2 MB)."];
        }
        else if (file.Length > MaxCvBytes)
        {
            errors["cv"] = ["The CV must be 2 MB or smaller."];
        }
        else
        {
            fileName = SafeFileName(file.FileName);
            contentType = DocumentType(file.ContentType, fileName);
            if (contentType is null)
            {
                errors["cv"] = ["Upload your CV as a PDF, DOC, or DOCX file."];
            }
            else
            {
                await using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, http.RequestAborted);
                fileData = buffer.ToArray();
                if (!LooksLikeDocument(fileData, contentType))
                    errors["cv"] = ["That file does not look like a valid PDF or Word document."];
            }
        }

        var intro = form.Files.GetFile("intro");
        string? introName = null;
        string? introType = null;
        byte[]? introData = null;
        if (intro is not null && intro.Length > 0)
        {
            if (intro.Length > MaxRecordingBytes)
            {
                errors["intro"] = ["The recording must be 20 MB or smaller."];
            }
            else
            {
                introName = SafeFileName(intro.FileName, "self-introduction.webm");
                introType = RecordingMediaType(intro.ContentType, introName);
                if (introType is null)
                {
                    errors["intro"] = ["Upload your recording as MP3, M4A, WAV, OGG, WebM, MP4, or MOV."];
                }
                else
                {
                    await using var buffer = new MemoryStream();
                    await intro.CopyToAsync(buffer, http.RequestAborted);
                    introData = buffer.ToArray();
                    if (!LooksLikeMedia(introData, introType))
                        errors["intro"] = ["That file does not look like a valid audio or video recording."];
                }
            }
        }

        var quran = form.Files.GetFile("quran");
        string? quranName = null;
        string? quranType = null;
        byte[]? quranData = null;
        if (quran is null || quran.Length == 0)
        {
            errors["quran"] = ["Attach a Quran recitation (MP3, M4A, WAV, OGG, WebM, MP4, or MOV, up to 20 MB)."];
        }
        else if (quran.Length > MaxRecordingBytes)
        {
            errors["quran"] = ["The Quran recording must be 20 MB or smaller."];
        }
        else
        {
            quranName = SafeFileName(quran.FileName, "quran-recitation.webm");
            quranType = RecordingMediaType(quran.ContentType, quranName);
            if (quranType is null)
            {
                errors["quran"] = ["Upload your Quran recording as MP3, M4A, WAV, OGG, WebM, MP4, or MOV."];
            }
            else
            {
                await using var buffer = new MemoryStream();
                await quran.CopyToAsync(buffer, http.RequestAborted);
                quranData = buffer.ToArray();
                if (!LooksLikeMedia(quranData, quranType))
                    errors["quran"] = ["That file does not look like a valid audio or video recording."];
            }
        }

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (await db.TeacherApplications.AnyAsync(x => x.Email == email && x.Status == "pending", http.RequestAborted))
            return Results.Conflict(new { message = "You already have an application in review. We will contact you soon." });
        if (await users.FindByEmailAsync(email) is not null)
            return Results.Conflict(new { message = "An account with this email already exists. Sign in or contact us if you need help." });

        var application = new TeacherApplication
        {
            FullName = fullName,
            Email = email,
            PhoneNumber = phone!,
            Subjects = string.Join(',', subjects),
            EnglishLevel = englishLevel,
            ExperienceYears = experienceYears,
            Bio = bio,
            AvailabilityNote = availabilityNote,
            Cv = new TeacherApplicationCv
            {
                FileName = fileName!,
                ContentType = contentType!,
                Data = fileData!,
            },
            Intro = introData is null
                ? null
                : new TeacherApplicationIntro
                {
                    FileName = introName!,
                    ContentType = introType!,
                    Data = introData,
                },
            QuranRecording = new TeacherApplicationQuranRecording
            {
                FileName = quranName!,
                ContentType = quranType!,
                Data = quranData!,
            },
        };
        db.TeacherApplications.Add(application);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new { message = "You already have an application in review. We will contact you soon." });
        }

        return Results.Json(new { id = application.Id, status = application.Status }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListApplicationsAsync(
        [FromServices] AcademyDbContext db,
        string? status,
        int? take)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not null && normalizedStatus is not ("pending" or "approved" or "rejected"))
            return Results.BadRequest(new { message = "Status must be pending, approved, or rejected." });

        var limit = Math.Clamp(take ?? 50, 1, 100);
        var query = db.TeacherApplications.AsNoTracking().AsQueryable();
        if (normalizedStatus is not null) query = query.Where(x => x.Status == normalizedStatus);

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.FullName,
                x.Email,
                x.PhoneNumber,
                x.Subjects,
                x.EnglishLevel,
                x.ExperienceYears,
                x.Bio,
                x.AvailabilityNote,
                x.Status,
                x.RejectionReason,
                x.CreatedAt,
                x.ReviewedAt,
                x.TeacherId,
                CvFileName = x.Cv == null ? null : x.Cv.FileName,
                IntroFileName = x.Intro == null ? null : x.Intro.FileName,
                QuranRecordingFileName = x.QuranRecording == null ? null : x.QuranRecording.FileName,
            })
            .ToListAsync();

        return Results.Ok(rows.Select(x => new TeacherApplicationDto(
            x.Id, x.FullName, x.Email, x.PhoneNumber,
            x.Subjects.Split(',', StringSplitOptions.RemoveEmptyEntries),
            x.EnglishLevel, x.ExperienceYears, x.Bio, x.AvailabilityNote, x.Status, x.RejectionReason,
            x.CreatedAt, x.ReviewedAt, x.TeacherId, x.CvFileName, x.IntroFileName, x.QuranRecordingFileName)).ToList());
    }

    private static async Task<IResult> DownloadCvAsync(Guid id, [FromServices] AcademyDbContext db)
    {
        var cv = await db.TeacherApplicationCvs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ApplicationId == id);
        if (cv is null) return Results.NotFound();
        return Results.File(cv.Data, cv.ContentType, SafeFileName(cv.FileName));
    }

    private static async Task<IResult> DownloadIntroAsync(Guid id, [FromServices] AcademyDbContext db)
    {
        var intro = await db.TeacherApplicationIntros.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ApplicationId == id);
        if (intro is null) return Results.NotFound();
        return Results.File(intro.Data, intro.ContentType, SafeFileName(intro.FileName, "self-introduction.webm"));
    }

    private static async Task<IResult> DownloadQuranRecordingAsync(Guid id, [FromServices] AcademyDbContext db)
    {
        var recording = await db.TeacherApplicationQuranRecordings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ApplicationId == id);
        if (recording is null) return Results.NotFound();
        return Results.File(recording.Data, recording.ContentType, SafeFileName(recording.FileName, "quran-recitation.webm"));
    }

    private static async Task<IResult> ReviewApplicationAsync(
        Guid id,
        ReviewTeacherApplicationRequest input,
        ClaimsPrincipal principal,
        HttpContext http,
        [FromServices] AcademyDbContext db,
        [FromServices] UserManager<AcademyUser> users,
        [FromServices] PasswordResetStore resets,
        [FromServices] AcademyEmailSender email)
    {
        var next = (input.Status ?? "").Trim().ToLowerInvariant();
        if (next is not ("approved" or "rejected"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Status must be approved or rejected."] });

        var reason = input.Reason?.Trim();
        if (next == "rejected" && (reason is null || reason.Length < 3))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Give a short reason (at least 3 characters)."] });
        if (reason?.Length > 500)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Keep the reason under 500 characters."] });

        var application = await db.TeacherApplications.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (application is null) return Results.NotFound();
        if (application.Status != "pending")
            return Results.Conflict(new { message = "This application was already reviewed." });

        string? inviteCode = null;

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);

        if (next == "approved")
        {
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
            var created = await users.CreateAsync(teacher, GenerateTemporaryPassword());
            if (!created.Succeeded)
                return Results.ValidationProblem(ApiEndpoints.IdentityErrors(created));

            var roleAdded = await users.AddToRoleAsync(teacher, "teacher");
            if (!roleAdded.Succeeded)
            {
                await users.DeleteAsync(teacher);
                return Results.Problem("Could not assign the teacher role. Please try again.", statusCode: StatusCodes.Status500InternalServerError);
            }

            application.TeacherId = teacher.Id;
            inviteCode = resets.TryIssue(application.Email.ToLowerInvariant());
        }

        application.Status = next;
        application.ReviewedBy = ApiEndpoints.UserId(principal);
        application.ReviewedAt = DateTimeOffset.UtcNow;
        application.RejectionReason = next == "rejected" ? reason : null;

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
        var inviteEmailed = false;
        if (inviteCode is not null)
            inviteEmailed = await email.SendTeacherInviteCodeAsync(application.Email, application.FullName, inviteCode, 15, http.RequestAborted);

        var files = await db.TeacherApplications.AsNoTracking()
            .Where(x => x.Id == application.Id)
            .Select(x => new
            {
                CvFileName = x.Cv == null ? null : x.Cv.FileName,
                IntroFileName = x.Intro == null ? null : x.Intro.FileName,
                QuranRecordingFileName = x.QuranRecording == null ? null : x.QuranRecording.FileName,
            })
            .FirstAsync(http.RequestAborted);
        var dto = new TeacherApplicationDto(
            application.Id, application.FullName, application.Email, application.PhoneNumber,
            application.Subjects.Split(',', StringSplitOptions.RemoveEmptyEntries),
            application.EnglishLevel, application.ExperienceYears, application.Bio, application.AvailabilityNote,
            application.Status, application.RejectionReason, application.CreatedAt,
            application.ReviewedAt, application.TeacherId, files.CvFileName, files.IntroFileName,
            files.QuranRecordingFileName);

        return Results.Ok(new
        {
            application = dto,
            inviteEmailed,
            inviteCode = inviteEmailed ? null : inviteCode,
        });
    }

    private static string GenerateTemporaryPassword()
    {
        // Satisfies the Identity policy (lowercase + digit, at least 8 chars).
        // The teacher never receives it; they set their own via the invite code.
        const string alphabet = "abcdefghijkmnopqrstuvwxyz23456789";
        var chars = new char[17];
        for (var i = 0; i < 16; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        chars[16] = '7';
        return new string(chars);
    }

    private static string SafeFileName(string name, string fallback = "application-cv.pdf")
    {
        var trimmed = Path.GetFileName(name);
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        if (cleaned.Length == 0) return fallback;
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string? DocumentType(string? rawContentType, string fileName)
    {
        var contentType = (rawContentType ?? "").Trim().ToLowerInvariant();
        if (contentType is "application/pdf" or "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return contentType;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => null,
        };
    }

    private static bool LooksLikeDocument(byte[] data, string contentType) => contentType switch
    {
        "application/pdf" => data.Length >= 5
            && data[0] == (byte)'%' && data[1] == (byte)'P' && data[2] == (byte)'D' && data[3] == (byte)'F' && data[4] == (byte)'-',
        "application/msword" => data.Length >= 8
            && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0
            && data[4] == 0xA1 && data[5] == 0xB1 && data[6] == 0x1A && data[7] == 0xE1,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => data.Length >= 4
            && data[0] == (byte)'P' && data[1] == (byte)'K' && data[2] == 3 && data[3] == 4,
        _ => false,
    };

    private static string? RecordingMediaType(string? rawContentType, string fileName)
    {
        var contentType = (rawContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (AllowedIntroContentTypes.Contains(contentType)) return contentType;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => null,
        };
    }

    private static bool LooksLikeMedia(byte[] data, string contentType)
    {
        if (data.Length < 12) return false;
        var ftyp = data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p';
        var riffWave = data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E';
        var ebml = data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3;
        var ogg = data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';
        return contentType switch
        {
            "audio/mpeg" => (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
                || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0),
            "audio/mp4" or "audio/x-m4a" or "video/mp4" or "video/quicktime" => ftyp,
            "audio/wav" or "audio/x-wav" => riffWave,
            "audio/ogg" => ogg,
            "audio/webm" or "video/webm" => ebml,
            _ => false,
        };
    }
}
