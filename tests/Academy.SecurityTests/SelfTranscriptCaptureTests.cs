using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class SelfTranscriptCaptureTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Theory]
    [InlineData("teacher")]
    [InlineData("student")]
    public async Task ParticipantIdentityAndProvenanceAreDerivedRatherThanTrusted(string role)
    {
        var (client, user, lesson) = await ParticipantAsync(role);
        using var owner = client;
        var segmentId = Guid.NewGuid();
        var impostor = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var response = await client.PostAsJsonAsync(SelfPath(lesson.Id), new
        {
            status = "started", speaker = "admin", source = "server", sourceUserId = impostor,
            userId = impostor, role = "admin", expected = false, segmentCount = 9000,
            segments = new[] { new
            {
                id = segmentId, text = "  Read the next verse.  ", atSeconds = 12.5,
                speaker = "admin", source = "server", sourceUserId = impostor, userId = impostor,
                flags = "payment", createdAt = DateTimeOffset.UtcNow.AddYears(-1)
            } }
        });
        await AssertAcceptedAsync(response, 1);

        var segment = await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().SingleAsync());
        Assert.Equal(lesson.Id, segment.LessonId);
        Assert.Equal(segmentId, segment.ClientSegmentId);
        Assert.Equal(user.Id, segment.SourceUserId);
        Assert.Equal(role, segment.Speaker);
        Assert.Equal("client", segment.Source);
        Assert.Equal("Read the next verse.", segment.Text);
        Assert.Equal(12.5, segment.AtSeconds);
        Assert.Equal("", segment.Flags);
        Assert.InRange(segment.CreatedAt, before, DateTimeOffset.UtcNow);
        var capture = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal(role, capture.Role);
        Assert.True(capture.Expected);
        Assert.Equal("started", capture.LastStatus);
        Assert.Equal(1, capture.SegmentCount);
        Assert.Equal(capture.FirstSeenAt, capture.LastSeenAt);
        Assert.Equal(capture.LastSeenAt, capture.LastSegmentAt);
        Assert.Equal(segment.CreatedAt, capture.FirstSeenAt);
    }

    [Fact]
    public async Task AnonymousCannotUploadEvenWithTheServerKey()
    {
        var (_, _, lesson) = await ParticipantAsync();
        using var client = Factory.Client;
        client.DefaultRequestHeaders.Add("X-Transcript-Key", ApiFactory.IngestKey);
        using var response = await UploadAsync(client, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Theory]
    [InlineData("teacher")]
    [InlineData("student")]
    [InlineData("admin")]
    [InlineData("support")]
    public async Task StrangersAndReviewersCannotUploadForParticipants(string role)
    {
        var (owner, _, lesson) = await ParticipantAsync();
        using var ownerClient = owner;
        using var stranger = await Factory.LoginAsync(role);
        using var response = await UploadAsync(stranger, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Theory]
    [InlineData("student", true)]
    [InlineData("teacher", false)]
    [InlineData("admin", true)]
    [InlineData("support", false)]
    public async Task MatchingParticipantIdWithoutTheExactRoleIsNotEnough(string role, bool teacherSlot)
    {
        EnableCapture();
        var (client, user) = await Factory.LoginAsAsync(role);
        using var owner = client;
        var other = await Factory.CreateUserAsync(teacherSlot ? "student" : "teacher");
        var lesson = await LiveLessonAsync(teacherSlot ? user.Id : other.Id, teacherSlot ? other.Id : user.Id);
        using var response = await UploadAsync(client, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Fact]
    public async Task DisabledCaptureAndUnknownLessonAreNotFound()
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        using var missing = await UploadAsync(client, Guid.NewGuid(), [Segment()]);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Config["LessonRoom:Transcription"] = "false";
        using var disabled = await UploadAsync(client, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Theory]
    [InlineData(16, "scheduled", false)]
    [InlineData(14, "scheduled", true)]
    [InlineData(-5, "scheduled", true)]
    [InlineData(-44, "completed", true)]
    [InlineData(-46, "completed", false)]
    [InlineData(-5, "cancelled", false)]
    public async Task UploadRespectsTheLessonWindowAndCancellation(int startsInMinutes, string status, bool allowed)
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        await MoveLessonAsync(lesson.Id, DateTimeOffset.UtcNow.AddMinutes(startsInMinutes), status);
        using var response = await UploadAsync(client, lesson.Id, [Segment()]);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        if (allowed) await AssertAcceptedAsync(response, 1);
        else await AssertNothingStoredAsync(lesson.Id);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("muted")]
    [InlineData("unsupported")]
    [InlineData("denied")]
    [InlineData("stopped")]
    [InlineData("error")]
    public async Task EveryCaptureStatusAllowsEmptyNullOrOmittedSegments(string status)
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        foreach (var payload in new object[]
        {
            new { status, segments = Array.Empty<object>() },
            new { status, segments = (object[]?)null },
            new { status }
        })
        {
            using var response = await client.PostAsJsonAsync(SelfPath(lesson.Id), payload);
            await AssertAcceptedAsync(response, 0);
        }
        var capture = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal(status, capture.LastStatus);
        Assert.True(capture.Expected);
        Assert.Equal(0, capture.SegmentCount);
        Assert.Null(capture.LastSegmentAt);
        Assert.True(capture.LastSeenAt >= capture.FirstSeenAt);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0, await StoredCountAsync(lesson.Id));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("muted")]
    [InlineData("stopped")]
    [InlineData("error")]
    public async Task TextMayAccompanySupportedStatuses(string status)
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        using var response = await UploadAsync(client, lesson.Id, [Segment()], status);
        await AssertAcceptedAsync(response, 1);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("denied")]
    public async Task UnsupportedOrDeniedCannotCarryText(string status)
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        using var response = await UploadAsync(client, lesson.Id, [Segment()], status);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Fact]
    public async Task InvalidStatusTextIdsAndBatchShapesAreRejectedWithoutPartialWrites()
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        var invalid = new List<object>
        {
            new { segments = new[] { Segment() } },
            new { status = (string?)null, segments = new[] { Segment() } },
            new { status = "", segments = new[] { Segment() } },
            new { status = "STARTED", segments = new[] { Segment() } },
            new { status = "awaiting", segments = new[] { Segment() } },
            new { status = " started ", segments = new[] { Segment() } },
            new { status = "started", segments = new object?[] { Segment(), null } },
            new { status = "started", segments = new[] { Segment(), Segment(text: "") } },
            new { status = "started", segments = new[] { Segment(), Segment(text: " \t\n ") } },
            new { status = "started", segments = new object[] { Segment(), new { id = Guid.NewGuid(), text = (string?)null } } },
            new { status = "started", segments = new object[] { Segment(), new { id = Guid.NewGuid() } } },
            new { status = "started", segments = new[] { Segment(), Segment(text: new string('x', 1001)) } },
            new { status = "started", segments = new[] { Segment(), Segment(id: Guid.Empty) } },
            new { status = "started", segments = new object[] { Segment(), new { text = "Missing UUID" } } },
            new { status = "started", segments = new object[] { new { id = "not-a-uuid", text = "Bad UUID" } } },
            new { status = "started", segments = new object[] { new { id = (string?)null, text = "Null UUID" } } },
            new { status = "started", segments = new { id = Guid.NewGuid(), text = "Not an array" } },
            new { status = "started", segments = Enumerable.Range(0, 51).Select(_ => Segment()).ToArray() }
        };
        foreach (var payload in invalid)
        {
            using var response = await client.PostAsJsonAsync(SelfPath(lesson.Id), payload);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"Payload {JsonSerializer.Serialize(payload)} returned {response.StatusCode}.");
            await AssertNothingStoredAsync(lesson.Id);
        }
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("3600.001")]
    [InlineData("1e400")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"not-a-number\"")]
    [InlineData("true")]
    public async Task NegativeOverlongAndNonfiniteTimestampsAreRejected(string jsonOffset)
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        // Raw JSON reaches the API even when the standard client serializer cannot encode the number.
        var json = $$"""{"status":"started","segments":[{"id":"{{Guid.NewGuid()}}","text":"Read.","atSeconds":{{jsonOffset}}}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(SelfPath(lesson.Id), content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Fact]
    public async Task InclusiveBatchTextAndTimestampBoundsAndOptionalTimestampAreAccepted()
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        var segments = Enumerable.Range(0, 50).Select(_ => Segment()).ToArray();
        segments[0] = Segment(text: "x", atSeconds: 0);
        segments[1] = Segment(text: new string('x', 1000), atSeconds: 3600);
        segments[2] = new { id = Guid.NewGuid(), text = "An optional timestamp." };
        segments[3] = Segment(atSeconds: null);
        using var response = await UploadAsync(client, lesson.Id, segments);
        await AssertAcceptedAsync(response, 50);
        var stored = await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(50, stored.Count);
        Assert.Contains(stored, x => x.Text.Length == 1 && x.AtSeconds == 0);
        Assert.Contains(stored, x => x.Text.Length == 1000 && x.AtSeconds == 3600);
        Assert.Equal(2, stored.Count(x => x.AtSeconds is null));
    }

    [Fact]
    public async Task ReplayDoesNotOverwriteTextOrIncrementCountersAndStatusOnlyPreservesLastSegmentTime()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        var id = Guid.NewGuid();
        using var first = await UploadAsync(client, lesson.Id, [Segment(id, "Original text.")]);
        await AssertAcceptedAsync(first, 1);
        var original = await CaptureAsync(lesson.Id, user.Id);
        using var replay = await UploadAsync(client, lesson.Id, [Segment(id, "Tampered replay.")], "muted");
        await AssertAcceptedAsync(replay, 0);
        var replayed = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal("muted", replayed.LastStatus);
        Assert.Equal(original.FirstSeenAt, replayed.FirstSeenAt);
        Assert.Equal(original.LastSegmentAt, replayed.LastSegmentAt);
        Assert.True(replayed.LastSeenAt > original.LastSeenAt);
        Assert.Equal(1, replayed.SegmentCount);

        var secondId = Guid.NewGuid();
        using var mixed = await UploadAsync(client, lesson.Id,
            [Segment(id), Segment(secondId, "Second text."), Segment(secondId, "Duplicate within batch.")]);
        await AssertAcceptedAsync(mixed, 1);
        var latest = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal(original.FirstSeenAt, latest.FirstSeenAt);
        Assert.Equal(2, latest.SegmentCount);
        Assert.True(latest.LastSegmentAt >= original.LastSegmentAt);
        using var stopped = await UploadAsync(client, lesson.Id, [], "stopped");
        await AssertAcceptedAsync(stopped, 0);
        var final = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal("stopped", final.LastStatus);
        Assert.Equal(latest.LastSegmentAt, final.LastSegmentAt);
        Assert.Equal(2, final.SegmentCount);
        var texts = await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().Select(x => x.Text).ToListAsync());
        Assert.Equal(new[] { "Original text.", "Second text." }, texts.OrderBy(x => x));
    }

    [Fact]
    public async Task ClientIdsAreScopedByBothLessonAndParticipant()
    {
        var (teacher, user, firstLesson) = await ParticipantAsync();
        using var teacherClient = teacher;
        var studentUser = await Factory.WithDbAsync(db => db.Users.SingleAsync(x => x.Id == firstLesson.StudentId));
        using var student = await Factory.SignInAsync(studentUser.Email);
        var secondLesson = await LiveLessonAsync(user.Id, studentUser.Id);
        var id = Guid.NewGuid();
        foreach (var (client, lesson) in new[] { (teacher, firstLesson), (student, firstLesson), (teacher, secondLesson) })
        {
            using var response = await UploadAsync(client, lesson.Id, [Segment(id)]);
            await AssertAcceptedAsync(response, 1);
        }
        Assert.Equal(3, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync(x => x.ClientSegmentId == id)));
        var captures = await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(3, captures.Count);
        Assert.All(captures, capture => Assert.Equal(1, capture.SegmentCount));
        Assert.Equal(2, captures.Count(x => x.LessonId == firstLesson.Id));
    }

    [Fact]
    public async Task CaptureCompositeKeyIsEnforcedByPostgres()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        using var response = await UploadAsync(client, lesson.Id, []);
        await AssertAcceptedAsync(response, 0);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Factory.WithDbAsync(async db =>
        {
            var key = db.Model.FindEntityType(typeof(LessonTranscriptCapture))!.FindPrimaryKey()!;
            Assert.Equal(new[] { "LessonId", "UserId" }, key.Properties.Select(x => x.Name));
            db.LessonTranscriptCaptures.Add(new LessonTranscriptCapture { LessonId = lesson.Id, UserId = user.Id, Role = "teacher" });
            await db.SaveChangesAsync();
        }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task ConcurrentReplaysStoreOneBatchAndOneCapture()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        var batch = Enumerable.Range(0, 10).Select(_ => Segment()).ToArray();
        var responses = await SimultaneouslyAsync(Enumerable.Range(0, 8)
            .Select(_ => (Func<Task<HttpResponseMessage>>)(() => UploadAsync(client, lesson.Id, batch))).ToArray());
        var accepted = new List<int>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                accepted.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
            }
        }
        Assert.Equal(1, accepted.Count(x => x == 10));
        Assert.Equal(7, accepted.Count(x => x == 0));
        Assert.Equal(10, await StoredCountAsync(lesson.Id));
        Assert.Equal(10, (await CaptureAsync(lesson.Id, user.Id)).SegmentCount);
    }

    [Fact]
    public async Task CombinedServerAndClientLimitIsInclusiveAndRejectsWholeBatches()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        using var relay = Factory.Client;
        await SeedSegmentsAsync(lesson, 4999);
        using var tooManySelf = await UploadAsync(client, lesson.Id, [Segment(), Segment()]);
        Assert.Equal(HttpStatusCode.Conflict, tooManySelf.StatusCode);
        using var tooManyRelay = await RelayAsync(relay, lesson.Id, 2);
        Assert.Equal(HttpStatusCode.Conflict, tooManyRelay.StatusCode);
        Assert.Equal(4999, await StoredCountAsync(lesson.Id));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync()));

        var lastId = Guid.NewGuid();
        using var last = await UploadAsync(client, lesson.Id, [Segment(lastId)]);
        await AssertAcceptedAsync(last, 1);
        using var replay = await UploadAsync(client, lesson.Id, [Segment(lastId)]);
        await AssertAcceptedAsync(replay, 0);
        using var status = await UploadAsync(client, lesson.Id, [], "stopped");
        await AssertAcceptedAsync(status, 0);
        var atCapacity = await CaptureAsync(lesson.Id, user.Id);
        using var mixed = await UploadAsync(client, lesson.Id, [Segment(lastId), Segment()]);
        Assert.Equal(HttpStatusCode.Conflict, mixed.StatusCode);
        using var relayOver = await RelayAsync(relay, lesson.Id, 1);
        Assert.Equal(HttpStatusCode.Conflict, relayOver.StatusCode);
        var unchanged = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal(atCapacity.LastSeenAt, unchanged.LastSeenAt);
        Assert.Equal("stopped", unchanged.LastStatus);
        Assert.Equal(1, unchanged.SegmentCount);
        Assert.Equal(5000, await StoredCountAsync(lesson.Id));
    }

    [Fact]
    public async Task ServerRelayCanFillTheLastSlotAfterClientSegments()
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        using var relay = Factory.Client;
        await SeedSegmentsAsync(lesson, 4998);
        using var self = await UploadAsync(client, lesson.Id, [Segment()]);
        await AssertAcceptedAsync(self, 1);
        using var last = await RelayAsync(relay, lesson.Id, 1);
        await AssertAcceptedAsync(last, 1);
        using var over = await UploadAsync(client, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal(5000, await StoredCountAsync(lesson.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimultaneousWritersCannotRacePastTheSharedCap(bool useServerRelay)
    {
        var (teacher, teacherUser, lesson) = await ParticipantAsync();
        using var owner = teacher;
        var studentUser = await Factory.WithDbAsync(db => db.Users.SingleAsync(x => x.Id == lesson.StudentId));
        using var other = useServerRelay ? Factory.Client : await Factory.SignInAsync(studentUser.Email);
        await SeedSegmentsAsync(lesson, 4999);
        var responses = await SimultaneouslyAsync(
            () => UploadAsync(teacher, lesson.Id, [Segment()]),
            () => useServerRelay ? RelayAsync(other, lesson.Id, 1) : UploadAsync(other, lesson.Id, [Segment()]));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            await AssertAcceptedAsync(responses.Single(x => x.IsSuccessStatusCode), 1);
            Assert.Equal(5000, await StoredCountAsync(lesson.Id));
            var captures = await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().ToListAsync());
            var clientWon = responses[0].IsSuccessStatusCode || !useServerRelay;
            Assert.Equal(clientWon ? 1 : 0, captures.Count);
            if (clientWon)
            {
                var capture = Assert.Single(captures);
                Assert.Equal(1, capture.SegmentCount);
                Assert.Equal(responses[0].IsSuccessStatusCode ? teacherUser.Id : studentUser.Id, capture.UserId);
            }
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task RateLimitIsSharedAcrossDevicesOfOneUserButNotTwoUsersAtTheSameIp()
    {
        var (teacher, user, lesson) = await ParticipantAsync();
        using var teacherClient = teacher;
        using var otherDevice = await Factory.SignInAsync(user.Email);
        var studentUser = await Factory.WithDbAsync(db => db.Users.SingleAsync(x => x.Id == lesson.StudentId));
        using var student = await Factory.SignInAsync(studentUser.Email);
        var otherLesson = await LiveLessonAsync(user.Id, studentUser.Id);
        for (var i = 0; i < 30; i++)
        {
            using var response = await UploadAsync(teacher, lesson.Id, []);
            await AssertAcceptedAsync(response, 0);
        }
        using var limited = await UploadAsync(teacher, lesson.Id, [Segment()]);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        using var sameUser = await UploadAsync(otherDevice, otherLesson.Id, []);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameUser.StatusCode);
        // RemoteAddressFilter supplies the same 127.0.0.1 for all these clients.
        using var differentUser = await UploadAsync(student, lesson.Id, [Segment()]);
        await AssertAcceptedAsync(differentUser, 1);
        Assert.Equal(0, (await CaptureAsync(lesson.Id, user.Id)).SegmentCount);
        Assert.Equal(1, (await CaptureAsync(lesson.Id, studentUser.Id)).SegmentCount);
        await AssertNothingStoredAsync(otherLesson.Id);
    }

    [Theory]
    [InlineData(-901, false)]
    [InlineData(-900, true)]
    [InlineData(0, true)]
    [InlineData(2700, true)]
    [InlineData(2701, false)]
    public void ExpectedWindowIncludesExactlyFifteenMinutesOnEitherSide(int secondsFromStart, bool expected)
    {
        var start = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var config = ExpectedConfig("true", start.AddDays(-1).ToString("O"));
        var lesson = new Lesson { StartsAt = start, EndsAt = start.AddMinutes(30) };
        Assert.Equal(expected, TranscriptCaptureEndpoints.IsExpected(config, lesson, start.AddSeconds(secondsFromStart)));
    }

    [Theory]
    [InlineData("true", "2026-07-01T12:00:00Z", true)]
    [InlineData("true", "2026-07-01T14:00:00+02:00", true)]
    [InlineData("true", "2026-07-01T12:00:01Z", false)]
    [InlineData("true", null, false)]
    [InlineData("true", "", false)]
    [InlineData("true", "not-a-date", false)]
    [InlineData("false", "2026-07-01T12:00:00Z", false)]
    public void ExpectedRequiresEnablementAndAParseableInclusiveRolloutDate(string enabled, string? since, bool expected)
    {
        var start = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var lesson = new Lesson { StartsAt = start, EndsAt = start.AddMinutes(30) };
        Assert.Equal(expected, TranscriptCaptureEndpoints.IsExpected(ExpectedConfig(enabled, since), lesson, start.AddMinutes(1)));
        if (expected)
            Assert.False(TranscriptCaptureEndpoints.IsExpected(ExpectedConfig(enabled, since), lesson, start.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("teacher")]
    [InlineData("student")]
    public async Task AttendanceJoinEnrollsExpectedCaptureAndRejoinsPreserveUploadedMetadata(string role)
    {
        var (client, user, lesson) = await ParticipantAsync(role);
        using var owner = client;
        using var joined = await AttendanceAsync(client, lesson.Id, "join");
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var expected = await CaptureAsync(lesson.Id, user.Id);
        Assert.True(expected.Expected);
        Assert.Equal("awaiting", expected.LastStatus);
        Assert.Equal(role, expected.Role);
        Assert.Equal(0, expected.SegmentCount);
        Assert.Null(expected.LastSegmentAt);
        using var uploaded = await UploadAsync(client, lesson.Id, [Segment()], "muted");
        await AssertAcceptedAsync(uploaded, 1);
        var captured = await CaptureAsync(lesson.Id, user.Id);
        using var rejoined = await AttendanceAsync(client, lesson.Id, "join");
        Assert.Equal(HttpStatusCode.OK, rejoined.StatusCode);
        var final = await CaptureAsync(lesson.Id, user.Id);
        Assert.Equal(expected.FirstSeenAt, final.FirstSeenAt);
        Assert.Equal(captured.LastSeenAt, final.LastSeenAt);
        Assert.Equal(captured.LastSegmentAt, final.LastSegmentAt);
        Assert.Equal("muted", final.LastStatus);
        Assert.Equal(1, final.SegmentCount);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync()));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("historical")]
    [InlineData("missing-since")]
    [InlineData("invalid-since")]
    [InlineData("too-early")]
    public async Task IneligibleAttendanceDoesNotRetroactivelyEnrollOnRead(string scenario)
    {
        var (client, _, lesson) = await ParticipantAsync();
        using var owner = client;
        switch (scenario)
        {
            case "disabled": Config["LessonRoom:Transcription"] = "false"; break;
            case "historical": Config["Transcripts:LightCaptureSince"] = DateTimeOffset.UtcNow.ToString("O"); break;
            case "missing-since": Config["Transcripts:LightCaptureSince"] = null; break;
            case "invalid-since": Config["Transcripts:LightCaptureSince"] = "not-a-date"; break;
            case "too-early": await MoveLessonAsync(lesson.Id, DateTimeOffset.UtcNow.AddMinutes(16)); break;
        }
        var eventId = Guid.NewGuid().ToString();
        using var joined = await AttendanceAsync(client, lesson.Id, "join", eventId);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
        EnableCapture();
        using var retry = await AttendanceAsync(client, lesson.Id, "join", eventId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await MoveLessonAsync(lesson.Id, DateTimeOffset.UtcNow.AddHours(-2), "completed");
        await Factory.WithDbAsync(db => db.LessonAttendances.Where(x => x.LessonId == lesson.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.TotalSeconds, 600)));
        using var reviewer = await Factory.LoginAsync("support");
        foreach (var path in new[] { "/api/admin/transcripts", "/api/admin/review-queue" })
            Assert.Empty((await GetJsonAsync(reviewer, path)).EnumerateArray());
        var workspace = await GetJsonAsync(reviewer, $"/api/admin/lessons/{lesson.Id}/review");
        Assert.Empty(workspace.GetProperty("captures").EnumerateArray());
        Assert.Empty(workspace.GetProperty("coverageReasons").EnumerateArray());
        await AssertNothingStoredAsync(lesson.Id);
    }

    [Fact]
    public async Task OnlyANewEnabledJoinEnrollsAndLeaveDoesNotEnroll()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        using var left = await AttendanceAsync(client, lesson.Id, "leave");
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
        Config["LessonRoom:Transcription"] = "false";
        using var disabled = await AttendanceAsync(client, lesson.Id, "join");
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await AssertNothingStoredAsync(lesson.Id);
        EnableCapture();
        using var joined = await AttendanceAsync(client, lesson.Id, "join");
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.True((await CaptureAsync(lesson.Id, user.Id)).Expected);
    }

    [Fact]
    public async Task CaptureExpectedCanBecomeTrueButCannotBeClearedByLaterConfiguration()
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        Config["Transcripts:LightCaptureSince"] = DateTimeOffset.UtcNow.ToString("O");
        using var historical = await UploadAsync(client, lesson.Id, []);
        await AssertAcceptedAsync(historical, 0);
        var initial = await CaptureAsync(lesson.Id, user.Id);
        Assert.False(initial.Expected);
        EnableCapture();
        using var enabled = await UploadAsync(client, lesson.Id, []);
        await AssertAcceptedAsync(enabled, 0);
        Assert.True((await CaptureAsync(lesson.Id, user.Id)).Expected);
        Config["Transcripts:LightCaptureSince"] = "invalid";
        using var later = await UploadAsync(client, lesson.Id, [], "error");
        await AssertAcceptedAsync(later, 0);
        var final = await CaptureAsync(lesson.Id, user.Id);
        Assert.True(final.Expected);
        Assert.Equal(initial.FirstSeenAt, final.FirstSeenAt);
    }

    [Theory]
    [InlineData("awaiting", "no_transcript")]
    [InlineData("started", "no_transcript")]
    [InlineData("muted", "no_transcript")]
    [InlineData("denied", "no_transcript")]
    [InlineData("stopped", "no_transcript")]
    [InlineData("error", "no_transcript")]
    [InlineData("unsupported", "unsupported_device")]
    public async Task ExpectedTeacherCoverageAppearsWithoutFabricatingFlaggedSegments(string status, string reason)
    {
        EnableCapture();
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var lesson = await CoverageLessonAsync(teacher.Id, student.Id, status: status);
        using var reviewer = await Factory.LoginAsync("support");
        var flagged = Assert.Single((await GetJsonAsync(reviewer, "/api/admin/transcripts")).EnumerateArray());
        Assert.Equal(lesson.Id, flagged.GetProperty("lessonId").GetGuid());
        Assert.Equal(0, flagged.GetProperty("flaggedSegments").GetInt32());
        Assert.Equal(new[] { reason }, Strings(flagged.GetProperty("reasons")));
        foreach (var query in new[] { "", "?status=unreviewed", "?status=flagged", "?flagged=true" })
        {
            var item = Assert.Single((await GetJsonAsync(reviewer, "/api/admin/review-queue" + query)).EnumerateArray());
            Assert.Equal(lesson.Id, item.GetProperty("lessonId").GetGuid());
            Assert.Equal(0, item.GetProperty("segments").GetInt32());
            Assert.Equal(0, item.GetProperty("flaggedSegments").GetInt32());
            Assert.Equal(new[] { reason }, Strings(item.GetProperty("flagReasons")));
            Assert.Equal(JsonValueKind.Null, item.GetProperty("reviewAverage").ValueKind);
        }
        var workspace = await GetJsonAsync(reviewer, $"/api/admin/lessons/{lesson.Id}/review");
        Assert.Equal(new[] { reason }, Strings(workspace.GetProperty("coverageReasons")));
        Assert.Equal(0, workspace.GetProperty("metrics").GetProperty("flaggedSegments").GetInt32());
        Assert.Empty(workspace.GetProperty("metrics").GetProperty("flagReasons").EnumerateArray());
        Assert.Empty(workspace.GetProperty("segments").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, workspace.GetProperty("review").ValueKind);
        var capture = Assert.Single(workspace.GetProperty("captures").EnumerateArray());
        Assert.Equal(status, capture.GetProperty("lastStatus").GetString());
        Assert.True(capture.GetProperty("expected").GetBoolean());
        Assert.Equal(0, await Factory.WithDbAsync(db => db.TeacherPenalties.CountAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.ReliabilityEvents.CountAsync()));
    }

    [Fact]
    public async Task CoverageRequiresEveryEligibilityConditionAndMatchesAttendanceToLessonAndUser()
    {
        EnableCapture();
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var valid = await CoverageLessonAsync(teacher.Id, student.Id);
        await CoverageLessonAsync(teacher.Id, student.Id, expected: false);
        await CoverageLessonAsync(teacher.Id, student.Id, role: "student");
        await CoverageLessonAsync(teacher.Id, student.Id, segmentCount: 1);
        await CoverageLessonAsync(teacher.Id, student.Id, seconds: 599);
        await CoverageLessonAsync(teacher.Id, student.Id, seconds: null);
        await CoverageLessonAsync(teacher.Id, student.Id, endedMinutesAgo: 14);
        await CoverageLessonAsync(teacher.Id, student.Id, lessonStatus: "cancelled");
        var wrongUser = await CoverageLessonAsync(teacher.Id, student.Id, seconds: null);
        await Factory.WithDbAsync(async db =>
        {
            db.LessonAttendances.Add(new LessonAttendance
                { LessonId = wrongUser.Id, UserId = student.Id, Role = "student", TotalSeconds = 600 });
            await db.SaveChangesAsync();
        });
        // The teacher's 600 seconds on 'valid' must not satisfy the other lesson's missing attendance.
        using var reviewer = await Factory.LoginAsync("support");
        foreach (var path in new[] { "/api/admin/transcripts", "/api/admin/review-queue" })
        {
            var item = Assert.Single((await GetJsonAsync(reviewer, path)).EnumerateArray());
            Assert.Equal(valid.Id, item.GetProperty("lessonId").GetGuid());
        }
    }

    [Fact]
    public async Task StudentTextDoesNotMaskTeacherCoverageAndRealFlagsSortAheadOfInformationalGaps()
    {
        EnableCapture();
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var gap = await CoverageLessonAsync(teacher.Id, student.Id, status: "unsupported");
        var flagged = await CoverageLessonAsync(teacher.Id, student.Id, expected: false, endedMinutesAgo: 60);
        await Factory.WithDbAsync(async db =>
        {
            db.LessonTranscriptSegments.AddRange(
                new LessonTranscriptSegment { LessonId = gap.Id, Source = "client", SourceUserId = student.Id, Speaker = "student", Text = "Read the verse." },
                new LessonTranscriptSegment { LessonId = flagged.Id, Speaker = "teacher", Text = "Pay me directly.", Flags = "payment" });
            await db.SaveChangesAsync();
        });
        using var reviewer = await Factory.LoginAsync("support");
        foreach (var path in new[] { "/api/admin/transcripts", "/api/admin/review-queue" })
        {
            var items = (await GetJsonAsync(reviewer, path)).EnumerateArray().ToArray();
            Assert.Equal(new[] { flagged.Id, gap.Id }, items.Select(x => x.GetProperty("lessonId").GetGuid()));
            Assert.Equal(1, items[0].GetProperty("flaggedSegments").GetInt32());
            Assert.Equal(0, items[1].GetProperty("flaggedSegments").GetInt32());
        }
        var workspace = await GetJsonAsync(reviewer, $"/api/admin/lessons/{gap.Id}/review");
        Assert.Equal(new[] { "unsupported_device" }, Strings(workspace.GetProperty("coverageReasons")));
        Assert.Equal(1, workspace.GetProperty("metrics").GetProperty("studentSegments").GetInt32());
    }

    [Theory]
    [InlineData("support")]
    [InlineData("admin")]
    public async Task ReviewWorkspaceReturnsCapturesSourcesAndAuditsViewsWithoutCopyingQuotes(string reviewerRole)
    {
        var (client, user, lesson) = await ParticipantAsync();
        using var owner = client;
        const string quote = "Please pay me directly. Private transcript quote.";
        using var upload = await UploadAsync(client, lesson.Id, [Segment(text: quote)]);
        await AssertAcceptedAsync(upload, 1);
        using var relay = Factory.Client;
        using var server = await RelayAsync(relay, lesson.Id, 1);
        await AssertAcceptedAsync(server, 1);
        var (reviewer, reviewerUser) = await Factory.LoginAsAsync(reviewerRole);
        using var reviewerClient = reviewer;
        foreach (var suffix in new[] { "review", "transcript" })
        {
            var workspace = await GetJsonAsync(reviewer, $"/api/admin/lessons/{lesson.Id}/{suffix}");
            Assert.Equal(30, workspace.GetProperty("retentionDays").GetInt32());
            Assert.Empty(workspace.GetProperty("coverageReasons").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, workspace.GetProperty("review").ValueKind);
            var capture = Assert.Single(workspace.GetProperty("captures").EnumerateArray());
            Assert.Equal("teacher", capture.GetProperty("role").GetString());
            Assert.Equal("started", capture.GetProperty("lastStatus").GetString());
            Assert.Equal(1, capture.GetProperty("segmentCount").GetInt32());
            Assert.True(capture.GetProperty("expected").GetBoolean());
            Assert.Equal(capture.GetProperty("firstSeenAt").GetDateTimeOffset(), capture.GetProperty("lastSeenAt").GetDateTimeOffset());
            Assert.Equal(capture.GetProperty("lastSeenAt").GetDateTimeOffset(), capture.GetProperty("lastSegmentAt").GetDateTimeOffset());
            var segments = workspace.GetProperty("segments").EnumerateArray().ToArray();
            Assert.Equal(new[] { "client", "server" }, segments.Select(x => x.GetProperty("source").GetString()).OrderBy(x => x));
            var self = Assert.Single(segments, x => x.GetProperty("source").GetString() == "client");
            Assert.Equal("teacher", self.GetProperty("speaker").GetString());
            Assert.Contains("payment", Strings(self.GetProperty("flags")));
        }
        await Factory.WithDbAsync(async db =>
        {
            var accesses = await db.TranscriptAccessLogs.Where(x => x.LessonId == lesson.Id).ToListAsync();
            Assert.Equal(2, accesses.Count);
            Assert.All(accesses, x => Assert.Equal(reviewerUser.Id, x.UserId));
            Assert.Equal(0, await db.LessonQualityReviews.CountAsync(x => x.LessonId == lesson.Id));
            Assert.False(await db.AuditLogs.AnyAsync(x => x.Detail.Contains(quote)));
            Assert.False(await db.Notifications.AnyAsync(x => x.Body.Contains(quote)));
            Assert.False(await db.LessonIncidents.AnyAsync(x => x.Detail.Contains(quote)));
        });
    }

    [Theory]
    [InlineData("teacher")]
    [InlineData("student")]
    [InlineData(null)]
    public async Task NonreviewersCannotReadOrSaveTranscriptWorkspaces(string? role)
    {
        var (owner, _, lesson) = await ParticipantAsync(role ?? "teacher");
        using var ownerClient = owner;
        using var caller = role is null ? Factory.Client : await Factory.SignInAsync(
            (await Factory.WithDbAsync(db => db.Users.SingleAsync(x => x.Id == (role == "teacher" ? lesson.TeacherId : lesson.StudentId)))).Email);
        var expected = role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        foreach (var path in new[] { "/api/admin/transcripts", "/api/admin/review-queue",
            $"/api/admin/lessons/{lesson.Id}/transcript", $"/api/admin/lessons/{lesson.Id}/review" })
        {
            using var response = await caller.GetAsync(path);
            Assert.Equal(expected, response.StatusCode);
        }
        using var save = await caller.PutAsJsonAsync($"/api/admin/lessons/{lesson.Id}/review", new { status = "draft", gaps = "Follow up." });
        Assert.Equal(expected, save.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.TranscriptAccessLogs.CountAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonQualityReviews.CountAsync()));
    }

    [Fact]
    public async Task NoTranscriptAllowsHumanDraftButRejectsSubmittedScoresWithoutChangingTheDraft()
    {
        EnableCapture();
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var lesson = await CoverageLessonAsync(teacher.Id, student.Id);
        using var reviewer = await Factory.LoginAsync("support");
        var path = $"/api/admin/lessons/{lesson.Id}/review";
        using var draft = await reviewer.PutAsJsonAsync(path, new
            { status = "draft", gaps = "Check capture availability.", teacherAction = "Ask about the device." });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        using var submitted = await reviewer.PutAsJsonAsync(path, new
        {
            status = "submitted", teachingClarity = 5, studentPractice = 5, correctionQuality = 5, engagement = 5,
            gaps = "Attempt to score without a transcript."
        });
        Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
        var problem = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("lesson", out _));
        var stored = await Factory.WithDbAsync(db => db.LessonQualityReviews.SingleAsync(x => x.LessonId == lesson.Id));
        Assert.Equal("draft", stored.Status);
        Assert.Equal("Check capture availability.", stored.Gaps);
        Assert.Equal("Ask about the device.", stored.TeacherAction);
        Assert.Null(stored.TeachingClarity);
        Assert.Null(stored.StudentPractice);
        Assert.Null(stored.CorrectionQuality);
        Assert.Null(stored.Engagement);
    }

    private IConfiguration Config => Factory.Services.GetRequiredService<IConfiguration>();

    private void EnableCapture()
    {
        Config["LessonRoom:Transcription"] = "true";
        Config["Transcripts:LightCaptureSince"] = DateTimeOffset.UtcNow.AddDays(-7).ToString("O");
    }

    private static IConfiguration ExpectedConfig(string enabled, string? since) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LessonRoom:Transcription"] = enabled, ["Transcripts:LightCaptureSince"] = since
        }).Build();

    private async Task<(HttpClient Client, AcademyUser User, Lesson Lesson)> ParticipantAsync(string role = "teacher")
    {
        EnableCapture();
        var (client, user) = await Factory.LoginAsAsync(role);
        var other = await Factory.CreateUserAsync(role == "teacher" ? "student" : "teacher");
        var lesson = await LiveLessonAsync(role == "teacher" ? user.Id : other.Id, role == "student" ? user.Id : other.Id);
        return (client, user, lesson);
    }

    private async Task<Lesson> LiveLessonAsync(Guid teacherId, Guid studentId)
    {
        var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var lesson = new Lesson { TeacherId = teacherId, StudentId = studentId, StartsAt = starts, EndsAt = starts.AddMinutes(30) };
        await Factory.WithDbAsync(async db => { db.Lessons.Add(lesson); await db.SaveChangesAsync(); });
        return lesson;
    }

    private Task MoveLessonAsync(Guid id, DateTimeOffset starts, string status = "scheduled") => Factory.WithDbAsync(db =>
        db.Lessons.Where(x => x.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.StartsAt, starts).SetProperty(x => x.EndsAt, starts.AddMinutes(30)).SetProperty(x => x.Status, status)));

    private async Task<Lesson> CoverageLessonAsync(Guid teacherId, Guid studentId, string status = "awaiting",
        bool expected = true, string role = "teacher", int segmentCount = 0, int? seconds = 600,
        int endedMinutesAgo = 16, string lessonStatus = "completed")
    {
        var end = DateTimeOffset.UtcNow.AddMinutes(-endedMinutesAgo);
        var lesson = new Lesson { TeacherId = teacherId, StudentId = studentId, StartsAt = end.AddMinutes(-30), EndsAt = end, Status = lessonStatus };
        var userId = role == "teacher" ? teacherId : studentId;
        await Factory.WithDbAsync(async db =>
        {
            db.Lessons.Add(lesson);
            db.LessonTranscriptCaptures.Add(new LessonTranscriptCapture
            {
                LessonId = lesson.Id, UserId = userId, Role = role, Expected = expected,
                LastStatus = status, SegmentCount = segmentCount, FirstSeenAt = lesson.StartsAt, LastSeenAt = end
            });
            if (seconds is not null)
                db.LessonAttendances.Add(new LessonAttendance { LessonId = lesson.Id, UserId = userId, Role = role, TotalSeconds = seconds.Value });
            await db.SaveChangesAsync();
        });
        return lesson;
    }

    private Task SeedSegmentsAsync(Lesson lesson, int count) => Factory.WithDbAsync(async db =>
    {
        db.LessonTranscriptSegments.AddRange(Enumerable.Range(0, count).Select(i => new LessonTranscriptSegment
        {
            LessonId = lesson.Id, Speaker = "teacher", Text = "Seeded segment.",
            Source = i % 2 == 0 ? "server" : "client",
            SourceUserId = i % 2 == 0 ? null : lesson.TeacherId,
            ClientSegmentId = i % 2 == 0 ? null : Guid.NewGuid()
        }));
        await db.SaveChangesAsync();
    });

    private Task<LessonTranscriptCapture> CaptureAsync(Guid lessonId, Guid userId) => Factory.WithDbAsync(db =>
        db.LessonTranscriptCaptures.IgnoreQueryFilters().SingleAsync(x => x.LessonId == lessonId && x.UserId == userId));

    private Task<int> StoredCountAsync(Guid lessonId) => Factory.WithDbAsync(db =>
        db.LessonTranscriptSegments.IgnoreQueryFilters().CountAsync(x => x.LessonId == lessonId));

    private async Task AssertNothingStoredAsync(Guid lessonId)
    {
        Assert.Equal(0, await StoredCountAsync(lessonId));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptCaptures.IgnoreQueryFilters().CountAsync(x => x.LessonId == lessonId)));
    }

    private static object Segment(Guid? id = null, string text = "Read the next verse.", double? atSeconds = 0) =>
        new { id = id ?? Guid.NewGuid(), text, atSeconds };

    private static string SelfPath(Guid lessonId) => $"/api/lessons/{lessonId}/transcript/self";

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid lessonId, object[] segments, string status = "started") =>
        client.PostAsJsonAsync(SelfPath(lessonId), new { segments, status });

    private static Task<HttpResponseMessage> AttendanceAsync(HttpClient client, Guid lessonId, string action, string? eventId = null) =>
        client.PostAsJsonAsync($"/api/lessons/{lessonId}/attendance/events", new { action, eventId = eventId ?? Guid.NewGuid().ToString(), deviceId = "test-device" });

    private static async Task<HttpResponseMessage> RelayAsync(HttpClient client, Guid lessonId, int count)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/lessons/{lessonId}/transcript")
        {
            Content = JsonContent.Create(new { segments = Enumerable.Range(0, count).Select(_ => new { speaker = "teacher", text = "Server relay text.", atSeconds = 1 }) })
        };
        request.Headers.Add("X-Transcript-Key", ApiFactory.IngestKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage[]> SimultaneouslyAsync(params Func<Task<HttpResponseMessage>>[] send)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = send.Select(async request => { await gate.Task; return await request(); }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task AssertAcceptedAsync(HttpResponseMessage response, int count)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(count, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()).ToArray();
}
