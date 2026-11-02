using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// Support-team lesson reviews: read a transcript, score the lesson on a fixed
/// rubric, record gaps and follow-up actions, then roll the week up per teacher.
/// Automated metrics give the reviewer a starting point; scores and notes stay
/// human. Every transcript view is written to TranscriptAccessLogs.
/// </summary>
public static class LessonReviewEndpoints
{
    public static void MapLessonReviews(this WebApplication app)
    {
        var reviewer = app.MapGroup("/api/admin").RequireAuthorization("ReviewerOnly");

        // Flagged lessons remain the fastest way to catch off-platform contact,
        // but the queue below covers every lesson that has a transcript.
        reviewer.MapGet("/transcripts", FlaggedLessonsAsync);
        reviewer.MapGet("/review-queue", ReviewQueueAsync);
        reviewer.MapGet("/review-digest", ReviewDigestAsync);
        reviewer.MapGet("/lessons/{id:guid}/transcript", TranscriptAsync);
        reviewer.MapGet("/lessons/{id:guid}/review", ReviewWorkspaceAsync);
        reviewer.MapPut("/lessons/{id:guid}/review", SaveReviewAsync);
    }

    private static async Task<IResult> FlaggedLessonsAsync(AcademyDbContext db)
    {
        var grouped = await db.LessonTranscriptSegments
            .Where(x => x.Flags != "")
            .GroupBy(x => x.LessonId)
            .Select(g => new { LessonId = g.Key, Flagged = g.Count(), LastFlaggedAt = g.Max(x => x.CreatedAt) })
            .OrderByDescending(x => x.LastFlaggedAt)
            .Take(200)
            .ToListAsync();
        if (grouped.Count == 0) return Results.Ok(Array.Empty<object>());
        var ids = grouped.Select(x => x.LessonId).ToList();
        var lessons = await db.Lessons.Include(x => x.Teacher).Include(x => x.Student)
            .Where(x => ids.Contains(x.Id)).ToListAsync();
        var rawFlags = await db.LessonTranscriptSegments
            .Where(x => ids.Contains(x.LessonId) && x.Flags != "")
            .Select(x => new { x.LessonId, x.Flags })
            .ToListAsync();
        return Results.Ok(grouped.Select(g =>
        {
            var lesson = lessons.FirstOrDefault(l => l.Id == g.LessonId);
            var reasons = rawFlags.Where(f => f.LessonId == g.LessonId)
                .SelectMany(f => f.Flags.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Distinct().OrderBy(x => x).ToArray();
            return new
            {
                lessonId = g.LessonId,
                startsAt = lesson?.StartsAt,
                status = lesson?.Status,
                teacherName = lesson?.Teacher?.FullName,
                studentName = lesson?.Student?.FullName,
                flaggedSegments = g.Flagged,
                lastFlaggedAt = g.LastFlaggedAt,
                reasons
            };
        }));
    }

    private static async Task<IResult> TranscriptAsync(Guid id, ClaimsPrincipal principal, AcademyDbContext db)
    {
        var payload = await LoadWorkspaceAsync(db, id);
        if (payload is null) return Results.NotFound();
        await LogAccessAsync(db, id, ApiEndpoints.UserId(principal));
        return Results.Ok(payload);
    }

    private static async Task<IResult> ReviewWorkspaceAsync(Guid id, ClaimsPrincipal principal, AcademyDbContext db)
    {
        var payload = await LoadWorkspaceAsync(db, id);
        if (payload is null) return Results.NotFound();
        await LogAccessAsync(db, id, ApiEndpoints.UserId(principal));
        return Results.Ok(payload);
    }

    private static async Task<object?> LoadWorkspaceAsync(AcademyDbContext db, Guid id)
    {
        var lesson = await db.Lessons.AsNoTracking()
            .Include(x => x.Teacher).Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (lesson is null) return null;

        var segments = await db.LessonTranscriptSegments.AsNoTracking()
            .Where(x => x.LessonId == id)
            .OrderBy(x => x.AtSeconds == null)
            .ThenBy(x => x.AtSeconds)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync();
        var review = await db.LessonQualityReviews.AsNoTracking()
            .Include(x => x.Reviewer)
            .FirstOrDefaultAsync(x => x.LessonId == id);

        return new
        {
            lesson = new
            {
                lesson.Id,
                lesson.TeacherId,
                lesson.StudentId,
                lesson.StartsAt,
                lesson.EndsAt,
                lesson.Status,
                teacherName = lesson.Teacher!.FullName,
                studentName = lesson.Student!.FullName
            },
            metrics = Metrics(segments, lesson.Teacher.FullName, lesson.Student.FullName),
            review = review is null ? null : ReviewPayload(review),
            segments = segments.Select(x => new
            {
                x.Id,
                x.Speaker,
                x.Text,
                x.AtSeconds,
                x.CreatedAt,
                flags = Flags(x.Flags)
            })
        };
    }

    private static async Task<IResult> ReviewQueueAsync(
        AcademyDbContext db,
        string? status,
        bool? flagged,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        if (normalized is not ("all" or "unreviewed" or "draft" or "submitted" or "flagged"))
            return Results.BadRequest(new { message = "Status must be all, unreviewed, draft, submitted, or flagged." });

        var query = db.Lessons.AsNoTracking()
            .Where(l => db.LessonTranscriptSegments.Any(s => s.LessonId == l.Id));
        if (from is not null) query = query.Where(l => l.StartsAt >= from);
        if (to is not null) query = query.Where(l => l.StartsAt < to);

        var lessons = await query
            .OrderByDescending(l => l.StartsAt)
            .Take(500)
            .Select(l => new QueueLesson(
                l.Id, l.StartsAt, l.Status, l.TeacherId, l.Teacher!.FullName,
                l.StudentId, l.Student!.FullName))
            .ToListAsync();
        if (lessons.Count == 0) return Results.Ok(Array.Empty<object>());

        var ids = lessons.Select(x => x.Id).ToList();
        var counts = await db.LessonTranscriptSegments.AsNoTracking()
            .Where(x => ids.Contains(x.LessonId))
            .GroupBy(x => x.LessonId)
            .Select(g => new
            {
                LessonId = g.Key,
                Segments = g.Count(),
                Flagged = g.Count(x => x.Flags != "")
            })
            .ToDictionaryAsync(x => x.LessonId);
        var flaggedSegments = await db.LessonTranscriptSegments.AsNoTracking()
            .Where(x => ids.Contains(x.LessonId) && x.Flags != "")
            .Select(x => new { x.LessonId, x.Flags })
            .ToListAsync();
        var reviews = await db.LessonQualityReviews.AsNoTracking()
            .Include(x => x.Reviewer)
            .Where(x => ids.Contains(x.LessonId))
            .ToDictionaryAsync(x => x.LessonId);

        var items = lessons.Select(lesson =>
        {
            counts.TryGetValue(lesson.Id, out var count);
            reviews.TryGetValue(lesson.Id, out var review);
            var reasons = flaggedSegments
                .Where(x => x.LessonId == lesson.Id)
                .SelectMany(x => Flags(x.Flags))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            return new QueueItem(
                lesson.Id,
                lesson.StartsAt,
                lesson.Status,
                lesson.TeacherId,
                lesson.TeacherName,
                lesson.StudentId,
                lesson.StudentName,
                count?.Segments ?? 0,
                count?.Flagged ?? 0,
                reasons,
                review?.Status,
                review is null ? null : AverageScore(review),
                review?.Reviewer?.FullName,
                review?.UpdatedAt);
        }).ToList();

        items = items.Where(item => normalized switch
        {
            "unreviewed" => item.ReviewStatus is null,
            "draft" => item.ReviewStatus == "draft",
            "submitted" => item.ReviewStatus == "submitted",
            "flagged" => item.FlaggedSegments > 0,
            _ => true
        }).ToList();
        if (flagged == true) items = items.Where(item => item.FlaggedSegments > 0).ToList();

        return Results.Ok(items
            .OrderByDescending(item => item.FlaggedSegments > 0)
            .ThenByDescending(item => item.StartsAt)
            .Take(200)
            .Select(item => new
            {
                lessonId = item.Id,
                startsAt = item.StartsAt,
                lessonStatus = item.Status,
                teacherId = item.TeacherId,
                teacherName = item.TeacherName,
                studentId = item.StudentId,
                studentName = item.StudentName,
                segments = item.Segments,
                flaggedSegments = item.FlaggedSegments,
                flagReasons = item.FlagReasons,
                reviewStatus = item.ReviewStatus,
                reviewAverage = item.ReviewAverage,
                reviewerName = item.ReviewerName,
                reviewUpdatedAt = item.ReviewUpdatedAt
            }));
    }

    private static async Task<IResult> ReviewDigestAsync(AcademyDbContext db, string? weekStart)
    {
        var start = ParseWeekStart(weekStart);
        var end = start.AddDays(7);

        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.StartsAt >= start && l.StartsAt < end
                && db.LessonTranscriptSegments.Any(s => s.LessonId == l.Id))
            .Select(l => new
            {
                l.Id,
                l.StartsAt,
                l.TeacherId,
                TeacherName = l.Teacher!.FullName,
                l.StudentId,
                StudentName = l.Student!.FullName
            })
            .ToListAsync();

        var ids = lessons.Select(x => x.Id).ToList();
        var reviews = await db.LessonQualityReviews.AsNoTracking()
            .Include(x => x.Reviewer)
            .Where(x => ids.Contains(x.LessonId))
            .ToDictionaryAsync(x => x.LessonId);
        var flagged = await db.LessonTranscriptSegments.AsNoTracking()
            .Where(x => ids.Contains(x.LessonId) && x.Flags != "")
            .GroupBy(x => x.LessonId)
            .Select(g => new { LessonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LessonId);

        var teachers = lessons
            .GroupBy(x => new { x.TeacherId, x.TeacherName })
            .OrderBy(g => g.Key.TeacherName)
            .Select(group =>
            {
                var rows = group.OrderBy(x => x.StartsAt).ToList();
                var submitted = rows
                    .Select(x => reviews.GetValueOrDefault(x.Id))
                    .OfType<LessonQualityReview>()
                    .Where(x => x.Status == "submitted")
                    .ToList();
                double? Average(Func<LessonQualityReview, int?> pick)
                {
                    var values = submitted.Select(pick).Where(x => x is not null).Select(x => (double)x!.Value).ToList();
                    return values.Count == 0 ? null : Math.Round(values.Average(), 1);
                }
                var actions = rows
                    .Select(x => new
                    {
                        Lesson = x,
                        Review = reviews.GetValueOrDefault(x.Id)
                    })
                    .Where(x => x.Review is { Status: "submitted" }
                        && (!string.IsNullOrWhiteSpace(x.Review.Gaps)
                            || !string.IsNullOrWhiteSpace(x.Review.TeacherAction)
                            || !string.IsNullOrWhiteSpace(x.Review.StudentAction)))
                    .Select(x => new
                    {
                        lessonId = x.Lesson.Id,
                        startsAt = x.Lesson.StartsAt,
                        studentName = x.Lesson.StudentName,
                        gaps = x.Review!.Gaps,
                        teacherAction = x.Review.TeacherAction,
                        studentAction = x.Review.StudentAction,
                        reviewerName = x.Review.Reviewer?.FullName
                    })
                    .ToList();
                var unreviewed = rows
                    .Where(x => reviews.GetValueOrDefault(x.Id) is not { Status: "submitted" })
                    .Select(x => new
                    {
                        lessonId = x.Id,
                        startsAt = x.StartsAt,
                        studentName = x.StudentName,
                        flaggedSegments = flagged.GetValueOrDefault(x.Id)?.Count ?? 0,
                        reviewStatus = reviews.GetValueOrDefault(x.Id)?.Status
                    })
                    .ToList();

                return new
                {
                    teacherId = group.Key.TeacherId,
                    teacherName = group.Key.TeacherName,
                    lessons = rows.Count,
                    reviewed = submitted.Count,
                    unreviewed = unreviewed.Count,
                    flaggedLessons = rows.Count(x => flagged.GetValueOrDefault(x.Id)?.Count > 0),
                    avgTeachingClarity = Average(x => x.TeachingClarity),
                    avgStudentPractice = Average(x => x.StudentPractice),
                    avgCorrectionQuality = Average(x => x.CorrectionQuality),
                    avgEngagement = Average(x => x.Engagement),
                    actions,
                    unreviewedLessons = unreviewed
                };
            })
            .ToList();

        return Results.Ok(new
        {
            weekStart = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weekEnd = end.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lessons = lessons.Count,
            reviewed = reviews.Values.Count(x => x.Status == "submitted"),
            flaggedLessons = flagged.Count,
            teachers
        });
    }

    private static async Task<IResult> SaveReviewAsync(
        Guid id,
        LessonQualityReviewInput input,
        ClaimsPrincipal principal,
        AcademyDbContext db)
    {
        var lessonExists = await db.Lessons.AsNoTracking().AnyAsync(x => x.Id == id);
        if (!lessonExists) return Results.NotFound();

        var status = string.IsNullOrWhiteSpace(input.Status) ? "draft" : input.Status.Trim().ToLowerInvariant();
        var errors = new Dictionary<string, string[]>();
        if (status is not ("draft" or "submitted"))
            errors["status"] = ["Status must be draft or submitted."];

        var scores = new (string Field, int? Value)[]
        {
            ("teachingClarity", input.TeachingClarity),
            ("studentPractice", input.StudentPractice),
            ("correctionQuality", input.CorrectionQuality),
            ("engagement", input.Engagement)
        };
        foreach (var (field, value) in scores)
            if (value is not null && value is < 1 or > 5)
                errors[field] = ["Scores range from 1 to 5."];
        if (status == "submitted" && scores.Any(x => x.Value is null))
            errors["scores"] = ["Score all four areas before submitting."];

        var gaps = input.Gaps?.Trim();
        if (status == "submitted" && (gaps is null || gaps.Length < 3))
            errors["gaps"] = ["Describe at least one gap before submitting."];
        if (input.Strengths?.Trim().Length > 2000) errors["strengths"] = ["Keep strengths under 2000 characters."];
        if (gaps?.Length > 2000) errors["gaps"] = ["Keep gaps under 2000 characters."];
        if (input.TeacherAction?.Trim().Length > 1000) errors["teacherAction"] = ["Keep the teacher action under 1000 characters."];
        if (input.StudentAction?.Trim().Length > 1000) errors["studentAction"] = ["Keep the student action under 1000 characters."];

        if (!await db.LessonTranscriptSegments.AsNoTracking().AnyAsync(x => x.LessonId == id))
            errors["lesson"] = ["This lesson has no transcript yet, so it cannot be reviewed."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var review = await db.LessonQualityReviews.FirstOrDefaultAsync(x => x.LessonId == id);
        var now = DateTimeOffset.UtcNow;
        if (review is null)
        {
            review = new LessonQualityReview
            {
                LessonId = id,
                ReviewerId = ApiEndpoints.UserId(principal),
                CreatedAt = now
            };
            db.LessonQualityReviews.Add(review);
        }
        review.TeachingClarity = input.TeachingClarity;
        review.StudentPractice = input.StudentPractice;
        review.CorrectionQuality = input.CorrectionQuality;
        review.Engagement = input.Engagement;
        review.Strengths = NullIfBlank(input.Strengths);
        review.Gaps = NullIfBlank(gaps);
        review.TeacherAction = NullIfBlank(input.TeacherAction);
        review.StudentAction = NullIfBlank(input.StudentAction);
        review.Status = status;
        review.UpdatedAt = now;
        await db.SaveChangesAsync();

        return Results.Ok(new { id = review.Id, lessonId = review.LessonId, status = review.Status, updatedAt = review.UpdatedAt });
    }

    private static object ReviewPayload(LessonQualityReview review) => new
    {
        review.Id,
        review.LessonId,
        review.ReviewerId,
        reviewerName = review.Reviewer?.FullName ?? "",
        review.TeachingClarity,
        review.StudentPractice,
        review.CorrectionQuality,
        review.Engagement,
        review.Strengths,
        review.Gaps,
        review.TeacherAction,
        review.StudentAction,
        review.Status,
        review.CreatedAt,
        review.UpdatedAt
    };

    private static double? AverageScore(LessonQualityReview review)
    {
        var values = new[] { review.TeachingClarity, review.StudentPractice, review.CorrectionQuality, review.Engagement }
            .Where(x => x is not null)
            .Select(x => (double)x!.Value)
            .ToList();
        return values.Count == 0 ? null : Math.Round(values.Average(), 1);
    }

    /// <summary>
    /// Rough first-pass metrics. Talk time is estimated from word count (~150 wpm)
    /// because timestamps are optional; it is a conversation-balance signal, not a
    /// precise clock. Speakers are matched to "teacher"/"student" labels or names.
    /// ASR text cannot judge tajweed correctness, so this never scores recitation.
    /// </summary>
    private static object Metrics(IReadOnlyList<LessonTranscriptSegment> segments, string teacherName, string studentName)
    {
        double teacherSeconds = 0, studentSeconds = 0;
        int teacher = 0, student = 0, unknown = 0;
        foreach (var segment in segments)
        {
            var words = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var seconds = Math.Max(1, words / 2.5);
            if (IsSpeaker(segment.Speaker, "teacher", teacherName))
            {
                teacher++;
                teacherSeconds += seconds;
            }
            else if (IsSpeaker(segment.Speaker, "student", studentName))
            {
                student++;
                studentSeconds += seconds;
            }
            else unknown++;
        }
        var knownSeconds = teacherSeconds + studentSeconds;
        var stamps = segments.Where(x => x.AtSeconds is not null).Select(x => x.AtSeconds!.Value).ToList();
        return new
        {
            segments = segments.Count,
            teacherSegments = teacher,
            studentSegments = student,
            unknownSegments = unknown,
            teacherTalkPercent = knownSeconds <= 0 ? (int?)null : (int)Math.Round(teacherSeconds / knownSeconds * 100),
            studentTalkPercent = knownSeconds <= 0 ? (int?)null : (int)Math.Round(studentSeconds / knownSeconds * 100),
            recordedSeconds = stamps.Count < 2 ? (double?)null : Math.Round(stamps.Max() - stamps.Min()),
            flaggedSegments = segments.Count(x => x.Flags != ""),
            flagReasons = segments
                .SelectMany(x => Flags(x.Flags))
                .Distinct()
                .OrderBy(x => x)
                .ToArray()
        };
    }

    private static bool IsSpeaker(string speaker, string role, string name) =>
        speaker.Contains(role, StringComparison.OrdinalIgnoreCase)
        || (name.Length > 2 && speaker.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static string[] Flags(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task LogAccessAsync(AcademyDbContext db, Guid lessonId, Guid userId)
    {
        db.TranscriptAccessLogs.Add(new TranscriptAccessLog { LessonId = lessonId, UserId = userId });
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset ParseWeekStart(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return new DateTimeOffset(today.AddDays(-daysSinceMonday).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private sealed record QueueLesson(
        Guid Id,
        DateTimeOffset StartsAt,
        string Status,
        Guid TeacherId,
        string TeacherName,
        Guid StudentId,
        string StudentName);

    private sealed record QueueItem(
        Guid Id,
        DateTimeOffset StartsAt,
        string Status,
        Guid TeacherId,
        string TeacherName,
        Guid StudentId,
        string StudentName,
        int Segments,
        int FlaggedSegments,
        string[] FlagReasons,
        string? ReviewStatus,
        double? ReviewAverage,
        string? ReviewerName,
        DateTimeOffset? ReviewUpdatedAt);
}
