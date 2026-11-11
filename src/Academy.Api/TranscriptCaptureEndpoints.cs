using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed record SelfTranscriptSegment(Guid Id, string? Text, double? AtSeconds);
public sealed record SelfTranscriptInput(IReadOnlyList<SelfTranscriptSegment?>? Segments, string? Status);

public static class TranscriptCaptureEndpoints
{
    public const int MaxSegmentsPerLesson = 5000;

    public static void MapTranscriptCapture(this WebApplication app) =>
        app.MapPost("/api/lessons/{id:guid}/transcript/self", UploadAsync)
            .RequireAuthorization()
            .RequireRateLimiting("self-transcripts")
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

    public static bool IsExpected(IConfiguration config, Lesson lesson, DateTimeOffset now) =>
        config.GetValue<bool>("LessonRoom:Transcription")
        && DateTimeOffset.TryParse(config["Transcripts:LightCaptureSince"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var since)
        && lesson.StartsAt >= since && now >= since
        && now >= lesson.StartsAt.AddMinutes(-15) && now <= lesson.EndsAt.AddMinutes(15);

    public static async Task ExpectAsync(
        AcademyDbContext db, IConfiguration config, Lesson lesson, Guid userId,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!IsExpected(config, lesson, now)) return;
        var role = userId == lesson.TeacherId ? "teacher" : "student";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LessonTranscriptCaptures"
                ("LessonId", "UserId", "Role", "LastStatus", "Expected", "FirstSeenAt", "LastSeenAt", "SegmentCount")
            VALUES ({lesson.Id}, {userId}, {role}, 'awaiting', TRUE, {now}, {now}, 0)
            ON CONFLICT ("LessonId", "UserId") DO UPDATE SET "Expected" = TRUE
            """, ct);
    }

    private static async Task<IResult> UploadAsync(
        Guid id, SelfTranscriptInput input, ClaimsPrincipal principal, AcademyDbContext db,
        IConfiguration config, CancellationToken ct)
    {
        if (!config.GetValue<bool>("LessonRoom:Transcription")) return Results.NotFound();
        var userId = ApiEndpoints.UserId(principal);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Both ingest routes lock the lesson so concurrent uploads share one hard cap.
        var lesson = await db.Lessons.FromSqlInterpolated($"""
            SELECT * FROM "Lessons" WHERE "Id" = {id} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (lesson is null) return Results.NotFound();
        var role = lesson.TeacherId == userId && principal.IsInRole("teacher") ? "teacher"
            : lesson.StudentId == userId && principal.IsInRole("student") ? "student" : null;
        if (role is null) return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        if (lesson.Status == "cancelled" || now < lesson.StartsAt.AddMinutes(-15) || now > lesson.EndsAt.AddMinutes(15))
            return Results.Conflict(new { message = "Transcription is outside the lesson window." });

        var status = input.Status;
        if (status is not ("started" or "muted" or "unsupported" or "denied" or "stopped" or "error"))
            return Results.BadRequest(new { message = "Unknown capture status." });
        var incoming = input.Segments ?? [];
        if (incoming.Count > 50 || (incoming.Count > 0 && status is "unsupported" or "denied"))
            return Results.BadRequest(new { message = "Invalid transcript batch." });
        var maxOffset = (lesson.EndsAt - lesson.StartsAt).TotalSeconds + 1800;
        if (incoming.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Text)
            || x.Text.Trim().Length > 1000
            || (x.AtSeconds is double at && (!double.IsFinite(at) || at < 0 || at > maxOffset))))
            return Results.BadRequest(new { message = "Invalid transcript segment." });

        var unique = incoming.Select(x => x!).DistinctBy(x => x.Id).ToList();
        var ids = unique.Select(x => x.Id).ToList();
        var existing = await db.LessonTranscriptSegments.IgnoreQueryFilters()
            .Where(x => x.LessonId == id && x.SourceUserId == userId && ids.Contains(x.ClientSegmentId!.Value))
            .Select(x => x.ClientSegmentId!.Value).ToListAsync(ct);
        var fresh = unique.Where(x => !existing.Contains(x.Id)).ToList();
        var stored = await db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync(x => x.LessonId == id, ct);
        if (stored + fresh.Count > MaxSegmentsPerLesson)
            return Results.Conflict(new { message = "Transcript limit reached." });

        string? previous = null;
        foreach (var segment in fresh)
        {
            var text = segment.Text!.Trim();
            var flags = TranscriptFlags.Scan(previous is null ? text : $"{previous} {text}");
            previous = text;
            db.LessonTranscriptSegments.Add(new LessonTranscriptSegment
            {
                LessonId = id, Speaker = role, Source = "client", SourceUserId = userId,
                ClientSegmentId = segment.Id, Text = text, AtSeconds = segment.AtSeconds,
                Flags = string.Join(",", flags), CreatedAt = now
            });
        }
        var expected = IsExpected(config, lesson, now);
        DateTimeOffset? lastSegment = fresh.Count > 0 ? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LessonTranscriptCaptures"
                ("LessonId", "UserId", "Role", "LastStatus", "Expected", "FirstSeenAt", "LastSeenAt", "LastSegmentAt", "SegmentCount")
            VALUES ({id}, {userId}, {role}, {status}, {expected}, {now}, {now}, {lastSegment}, {fresh.Count})
            ON CONFLICT ("LessonId", "UserId") DO UPDATE SET
                "LastStatus" = EXCLUDED."LastStatus", "LastSeenAt" = EXCLUDED."LastSeenAt",
                "Expected" = "LessonTranscriptCaptures"."Expected" OR EXCLUDED."Expected",
                "LastSegmentAt" = COALESCE(EXCLUDED."LastSegmentAt", "LessonTranscriptCaptures"."LastSegmentAt"),
                "SegmentCount" = "LessonTranscriptCaptures"."SegmentCount" + EXCLUDED."SegmentCount"
            """, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { accepted = fresh.Count });
    }

    public static IQueryable<LessonTranscriptCapture> CoverageGaps(AcademyDbContext db)
    {
        var endedBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        return db.LessonTranscriptCaptures.AsNoTracking().Where(x => x.Expected && x.Role == "teacher"
            && x.SegmentCount == 0 && x.Lesson!.Status != "cancelled" && x.Lesson.EndsAt < endedBefore
            && db.LessonAttendances.Any(a => a.LessonId == x.LessonId && a.UserId == x.UserId && a.TotalSeconds >= 600));
    }

    public static string CoverageReason(string status) => status == "unsupported" ? "unsupported_device" : "no_transcript";
}
