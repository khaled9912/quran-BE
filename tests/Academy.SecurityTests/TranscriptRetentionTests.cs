using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class TranscriptRetentionTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task ThirtyDayFiltersHideExpiredFlaggedAndUnflaggedRowsBeforePhysicalDeletion()
    {
        var seed = await SeedRetentionAsync();
        await Factory.WithDbAsync(async db =>
        {
            Assert.Equal(6, await db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync());
            Assert.Equal(4, await db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync());
            var visibleSegments = await db.LessonTranscriptSegments.AsNoTracking().ToListAsync();
            Assert.Equal(2, visibleSegments.Count);
            Assert.All(visibleSegments, x => Assert.Equal(seed.RetainedLesson.Id, x.LessonId));
            var visibleCaptures = await db.LessonTranscriptCaptures.AsNoTracking().ToListAsync();
            Assert.Equal(2, visibleCaptures.Count);
            Assert.All(visibleCaptures, x => Assert.Equal(seed.RetainedLesson.Id, x.LessonId));
            // A recent heartbeat cannot extend retention of an old capture.
            var oldTeacher = await db.LessonTranscriptCaptures.IgnoreQueryFilters()
                .SingleAsync(x => x.LessonId == seed.ExpiredLesson.Id && x.UserId == seed.Teacher.Id);
            Assert.True(oldTeacher.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.True(oldTeacher.FirstSeenAt < DateTimeOffset.UtcNow.AddDays(-30));
            Assert.Empty(await TranscriptCaptureEndpoints.CoverageGaps(db).ToListAsync());
        });
    }

    [Fact]
    public async Task ReviewerListsWorkspacesAndMetricsNeverExposeExpiredTextOrCoverage()
    {
        var seed = await SeedRetentionAsync();
        using var reviewer = await Factory.LoginAsync("support");
        foreach (var path in new[] { "/api/admin/transcripts", "/api/admin/review-queue", "/api/admin/review-queue?flagged=true" })
        {
            var items = (await GetJsonAsync(reviewer, path)).EnumerateArray().ToArray();
            Assert.Equal(seed.RetainedLesson.Id, Assert.Single(items).GetProperty("lessonId").GetGuid());
            Assert.DoesNotContain(seed.ExpiredQuote, JsonSerializer.Serialize(items));
        }
        foreach (var suffix in new[] { "review", "transcript" })
        {
            var workspace = await GetJsonAsync(reviewer, $"/api/admin/lessons/{seed.ExpiredLesson.Id}/{suffix}");
            Assert.Empty(workspace.GetProperty("segments").EnumerateArray());
            Assert.Empty(workspace.GetProperty("captures").EnumerateArray());
            Assert.Empty(workspace.GetProperty("coverageReasons").EnumerateArray());
            Assert.Equal(0, workspace.GetProperty("metrics").GetProperty("segments").GetInt32());
            Assert.Equal(0, workspace.GetProperty("metrics").GetProperty("flaggedSegments").GetInt32());
            Assert.Equal(30, workspace.GetProperty("retentionDays").GetInt32());
            Assert.DoesNotContain(seed.ExpiredQuote, workspace.GetRawText());
        }
        var retained = await GetJsonAsync(reviewer, $"/api/admin/lessons/{seed.RetainedLesson.Id}/review");
        Assert.Equal(2, retained.GetProperty("segments").GetArrayLength());
        Assert.Equal(2, retained.GetProperty("captures").GetArrayLength());
        Assert.Equal(1, retained.GetProperty("metrics").GetProperty("flaggedSegments").GetInt32());
        // Read paths must filter, not depend on the hourly cleanup having happened.
        Assert.Equal(4, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters()
            .CountAsync(x => x.LessonId == seed.ExpiredLesson.Id)));
        Assert.Equal(2, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters()
            .CountAsync(x => x.LessonId == seed.ExpiredLesson.Id)));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task PurgePhysicallyDeletesAllExpiredSourcesAndFlagsButRetainsHumanReviewAndAudit(string enabled)
    {
        var seed = await SeedRetentionAsync();
        Config["LessonRoom:Transcription"] = enabled;
        Config["Transcripts:LightCaptureSince"] = null;
        Config["Transcripts:IngestKey"] = "";
        var reviewer = await Factory.CreateUserAsync("support");
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        var access = new TranscriptAccessLog { LessonId = seed.ExpiredLesson.Id, UserId = reviewer.Id, AccessedAt = old };
        var audit = new AuditLog
        {
            ActorId = reviewer.Id, ActorRole = "support", EntityType = "Lesson", EntityId = seed.ExpiredLesson.Id,
            Action = "human_follow_up", Detail = "Human follow-up was recorded.", CreatedAt = old
        };
        var reviews = new[]
        {
            new LessonQualityReview
            {
                LessonId = seed.ExpiredLesson.Id, ReviewerId = reviewer.Id, Status = "submitted",
                TeachingClarity = 4, StudentPractice = 3, CorrectionQuality = 5, Engagement = 4,
                Strengths = "Careful pacing.", Gaps = "Allow more practice time.",
                TeacherAction = "Pause after each verse.", StudentAction = "Practise the next page.",
                CreatedAt = old, UpdatedAt = old
            },
            new LessonQualityReview
            {
                LessonId = seed.RetainedLesson.Id, ReviewerId = reviewer.Id, Status = "draft",
                Gaps = "A human-authored draft.", CreatedAt = old, UpdatedAt = old
            }
        };
        await Factory.WithDbAsync(async db =>
        {
            db.LessonQualityReviews.AddRange(reviews);
            db.TranscriptAccessLogs.Add(access);
            db.AuditLogs.Add(audit);
            await db.SaveChangesAsync();
        });
        var beforeReviews = await Factory.WithDbAsync(db => db.LessonQualityReviews.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
        await Factory.WithDbAsync(db => TranscriptRetention.PurgeAsync(db));
        // Cleanup is safe to run repeatedly, regardless of flags or a previous successful pass.
        await Factory.WithDbAsync(db => TranscriptRetention.PurgeAsync(db));
        await Factory.WithDbAsync(async db =>
        {
            var segments = await db.LessonTranscriptSegments.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, segments.Count);
            Assert.All(segments, x => Assert.Equal(seed.RetainedLesson.Id, x.LessonId));
            var captures = await db.LessonTranscriptCaptures.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, captures.Count);
            Assert.All(captures, x => Assert.Equal(seed.RetainedLesson.Id, x.LessonId));
            var afterReviews = await db.LessonQualityReviews.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(JsonSerializer.Serialize(beforeReviews), JsonSerializer.Serialize(afterReviews));
            var keptAccess = await db.TranscriptAccessLogs.SingleAsync(x => x.Id == access.Id);
            Assert.Equal(reviewer.Id, keptAccess.UserId);
            Assert.Equal(old.ToUnixTimeSeconds(), keptAccess.AccessedAt.ToUnixTimeSeconds());
            var keptAudit = await db.AuditLogs.SingleAsync(x => x.Id == audit.Id);
            Assert.Equal(audit.Detail, keptAudit.Detail);
            Assert.Equal(old.ToUnixTimeSeconds(), keptAudit.CreatedAt.ToUnixTimeSeconds());
            Assert.Equal(2, await db.Lessons.CountAsync());
            Assert.Equal(1, await db.LessonAttendances.CountAsync(x => x.LessonId == seed.ExpiredLesson.Id));
            Assert.DoesNotContain(seed.ExpiredQuote, JsonSerializer.Serialize(afterReviews));
            Assert.False(await db.AuditLogs.AnyAsync(x => x.Detail.Contains(seed.ExpiredQuote)));
        });
        using var client = await Factory.SignInAsync(reviewer.Email);
        var workspace = await GetJsonAsync(client, $"/api/admin/lessons/{seed.ExpiredLesson.Id}/review");
        Assert.Empty(workspace.GetProperty("segments").EnumerateArray());
        Assert.Empty(workspace.GetProperty("captures").EnumerateArray());
        Assert.Equal("submitted", workspace.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("Allow more practice time.", workspace.GetProperty("review").GetProperty("gaps").GetString());
        Assert.DoesNotContain(seed.ExpiredQuote, workspace.GetRawText());
    }

    [Fact]
    public async Task RetentionIsBasedOnRowTimestampsRatherThanTheLessonAge()
    {
        var seed = await SeedRetentionAsync();
        await Factory.WithDbAsync(async db =>
        {
            await db.Lessons.Where(x => x.Id == seed.RetainedLesson.Id).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.StartsAt, DateTimeOffset.UtcNow.AddDays(-90))
                .SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddDays(-90).AddMinutes(30)));
            Assert.Equal(2, await db.LessonTranscriptSegments.CountAsync(x => x.LessonId == seed.RetainedLesson.Id));
            Assert.Equal(2, await db.LessonTranscriptCaptures.CountAsync(x => x.LessonId == seed.RetainedLesson.Id));
            await TranscriptRetention.PurgeAsync(db);
            Assert.Equal(2, await db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync(x => x.LessonId == seed.RetainedLesson.Id));
            Assert.Equal(2, await db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync(x => x.LessonId == seed.RetainedLesson.Id));
        });
    }

    [Fact]
    public async Task ExpiredTranscriptCannotJustifyNewScoresButHumanDraftCanStillBeSaved()
    {
        var seed = await SeedRetentionAsync();
        using var reviewer = await Factory.LoginAsync("support");
        var path = $"/api/admin/lessons/{seed.ExpiredLesson.Id}/review";
        using var submitted = await reviewer.PutAsJsonAsync(path, new
        {
            status = "submitted", teachingClarity = 4, studentPractice = 4, correctionQuality = 4, engagement = 4,
            gaps = "There is no longer a retained transcript."
        });
        Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
        var problem = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("lesson", out _));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonQualityReviews.CountAsync()));
        using var draft = await reviewer.PutAsJsonAsync(path, new
            { status = "draft", gaps = "Discuss the capture issue.", teacherAction = "Check microphone permissions." });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var saved = await Factory.WithDbAsync(db => db.LessonQualityReviews.SingleAsync());
        Assert.Equal("draft", saved.Status);
        Assert.Equal("Discuss the capture issue.", saved.Gaps);
        Assert.Null(saved.TeachingClarity);
        Assert.DoesNotContain(seed.ExpiredQuote, JsonSerializer.Serialize(saved));
        Assert.Equal(4, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters()
            .CountAsync(x => x.LessonId == seed.ExpiredLesson.Id)));
    }

    [Fact]
    public async Task HiddenRowsStillCountTowardsTheHardCapUntilPhysicallyPurged()
    {
        Config["LessonRoom:Transcription"] = "true";
        Config["Transcripts:LightCaptureSince"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var (teacher, user) = await Factory.LoginAsAsync("teacher");
        using var owner = teacher;
        var student = await Factory.CreateUserAsync("student");
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var lesson = await AddLessonAsync(user.Id, student.Id, start, "scheduled");
        var replayId = Guid.NewGuid();
        await Factory.WithDbAsync(async db =>
        {
            db.LessonTranscriptSegments.AddRange(Enumerable.Range(0, 5000).Select(i => new LessonTranscriptSegment
            {
                LessonId = lesson.Id, Source = i == 0 ? "client" : "server", Speaker = "teacher", Text = "Expired segment.",
                SourceUserId = i == 0 ? user.Id : null, ClientSegmentId = i == 0 ? replayId : null,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-31)
            }));
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.LessonTranscriptSegments.CountAsync());
        });
        var selfPath = $"/api/lessons/{lesson.Id}/transcript/self";
        using var replay = await teacher.PostAsJsonAsync(selfPath, new
            { status = "started", segments = new[] { new { id = replayId, text = "Must not replace expired text.", atSeconds = 0 } } });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        using var self = await teacher.PostAsJsonAsync(selfPath, new
            { status = "started", segments = new[] { new { id = Guid.NewGuid(), text = "New text.", atSeconds = 0 } } });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        using var relay = Factory.Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/lessons/{lesson.Id}/transcript")
        {
            Content = JsonContent.Create(new { segments = new[] { new { speaker = "teacher", text = "New relay text.", atSeconds = 0 } } })
        };
        request.Headers.Add("X-Transcript-Key", ApiFactory.IngestKey);
        using var server = await relay.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, server.StatusCode);
        Assert.Equal(5000, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.Select(x => x.SegmentCount).SingleAsync()));
        await Factory.WithDbAsync(db => TranscriptRetention.PurgeAsync(db));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync()));
        using var afterPurge = await teacher.PostAsJsonAsync(selfPath, new
            { status = "started", segments = new[] { new { id = Guid.NewGuid(), text = "Capacity is available again.", atSeconds = 0 } } });
        Assert.Equal(HttpStatusCode.OK, afterPurge.StatusCode);
        Assert.Equal(1, (await afterPurge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task ServerRelayCannotRepopulateLessonsThatEndedOverThirtyDaysAgo()
    {
        var seed = await SeedRetentionAsync();
        await Factory.WithDbAsync(db => db.Lessons.Where(x => x.Id == seed.ExpiredLesson.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.StartsAt, DateTimeOffset.UtcNow.AddDays(-31).AddMinutes(-30))
            .SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddDays(-31))));
        await Factory.WithDbAsync(db => TranscriptRetention.PurgeAsync(db));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/lessons/{seed.ExpiredLesson.Id}/transcript")
        {
            Content = JsonContent.Create(new { segments = new[] { new { speaker = "teacher", text = "Late relay.", atSeconds = 0 } } })
        };
        request.Headers.Add("X-Transcript-Key", ApiFactory.IngestKey);
        using var client = Factory.Client;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters()
            .CountAsync(x => x.LessonId == seed.ExpiredLesson.Id)));
    }

    private IConfiguration Config => Factory.Services.GetRequiredService<IConfiguration>();

    private async Task<RetentionSeed> SeedRetentionAsync()
    {
        Config["LessonRoom:Transcription"] = "true";
        Config["Transcripts:LightCaptureSince"] = DateTimeOffset.UtcNow.AddDays(-60).ToString("O");
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var now = DateTimeOffset.UtcNow;
        var expiredLesson = await AddLessonAsync(teacher.Id, student.Id, now.AddDays(-31));
        var retainedLesson = await AddLessonAsync(teacher.Id, student.Id, now.AddDays(-29));
        // One minute either side avoids coupling the test to scheduling and PostgreSQL's microsecond precision.
        var expired = now.AddDays(-30).AddMinutes(-1);
        var retained = now.AddDays(-30).AddMinutes(1);
        const string expiredQuote = "Expired private transcript quote.";
        await Factory.WithDbAsync(async db =>
        {
            foreach (var source in new[] { "server", "client" })
            foreach (var flags in new[] { "", "payment" })
                db.LessonTranscriptSegments.Add(new LessonTranscriptSegment
                {
                    LessonId = expiredLesson.Id, Speaker = "teacher", Source = source,
                    SourceUserId = source == "client" ? teacher.Id : null,
                    ClientSegmentId = source == "client" ? Guid.NewGuid() : null,
                    Text = expiredQuote, Flags = flags, CreatedAt = expired
                });
            foreach (var source in new[] { "server", "client" })
                db.LessonTranscriptSegments.Add(new LessonTranscriptSegment
                {
                    LessonId = retainedLesson.Id, Speaker = "teacher", Source = source,
                    SourceUserId = source == "client" ? teacher.Id : null,
                    ClientSegmentId = source == "client" ? Guid.NewGuid() : null,
                    Text = "Retained text.", Flags = source == "client" ? "phone" : "", CreatedAt = retained
                });
            foreach (var (lesson, firstSeen) in new[] { (expiredLesson, expired), (retainedLesson, retained) })
            foreach (var (userId, role) in new[] { (teacher.Id, "teacher"), (student.Id, "student") })
                db.LessonTranscriptCaptures.Add(new LessonTranscriptCapture
                {
                    LessonId = lesson.Id, UserId = userId, Role = role, Expected = true,
                    LastStatus = role == "teacher" ? "unsupported" : "denied", SegmentCount = 0,
                    FirstSeenAt = firstSeen, LastSeenAt = now, LastSegmentAt = null
                });
            // This would be a coverage gap if the expired capture leaked through its filter.
            db.LessonAttendances.Add(new LessonAttendance
                { LessonId = expiredLesson.Id, UserId = teacher.Id, Role = "teacher", TotalSeconds = 600 });
            await db.SaveChangesAsync();
        });
        return new RetentionSeed(teacher, expiredLesson, retainedLesson, expiredQuote);
    }

    private async Task<Lesson> AddLessonAsync(Guid teacherId, Guid studentId, DateTimeOffset start, string status = "completed")
    {
        var lesson = new Lesson { TeacherId = teacherId, StudentId = studentId, StartsAt = start, EndsAt = start.AddMinutes(30), Status = status };
        await Factory.WithDbAsync(async db => { db.Lessons.Add(lesson); await db.SaveChangesAsync(); });
        return lesson;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record RetentionSeed(AcademyUser Teacher, Lesson ExpiredLesson, Lesson RetainedLesson, string ExpiredQuote);
}
