using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The capture relay posts with a shared server key instead of a session, so the
/// key check and the per-lesson caps are the only thing guarding this table.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TranscriptIngestTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly List<object> Segments =
    [
        new { speaker = "Teacher", text = "Let us begin with Al-Fatiha.", atSeconds = 0.0 },
        new { speaker = "Student", text = "Okay.", atSeconds = 12.5 },
    ];

    [Fact]
    public async Task MissingKeyIsRejected()
    {
        var (lessonId, _) = await ParticipantsAsync();

        var response = await Factory.Client.PostAsJsonAsync(
            $"/api/lessons/{lessonId}/transcript", new { segments = Segments });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.CountAsync(x => x.LessonId == lessonId)));
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        var (lessonId, _) = await ParticipantsAsync();
        using var request = await BuildAsync(lessonId, "definitely-not-the-key", Segments);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CorrectKeyStoresSegments()
    {
        var (lessonId, _) = await ParticipantsAsync();
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey, Segments);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.CountAsync(x => x.LessonId == lessonId)));
    }

    [Fact]
    public async Task UnknownLessonIsNotFoundEvenWithTheKey()
    {
        using var request = await BuildAsync(Guid.NewGuid(), ApiFactory.IngestKey, Segments);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OversizedBatchIsRejected()
    {
        var (lessonId, _) = await ParticipantsAsync();
        var many = Enumerable.Range(0, 201)
            .Select(i => (object)new { speaker = "Teacher", text = $"Segment {i}.", atSeconds = (double)i })
            .ToList();
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey, many);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonTranscriptSegments.CountAsync(x => x.LessonId == lessonId)));
    }

    [Fact]
    public async Task EmptyBatchIsRejected()
    {
        var (lessonId, _) = await ParticipantsAsync();
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey, []);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExpandedReasonsAreStoredAndReturnedToReviewers()
    {
        var (lessonId, _) = await ParticipantsAsync();
        const string text = "Can you share your mobile number and share your email? Pay me directly and keep this between us.";
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey,
            [new { speaker = "Teacher", text, atSeconds = 10.0 }]);

        var response = await Factory.Client.SendAsync(request);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, result.GetProperty("flagged").GetInt32());
        var stored = await Factory.WithDbAsync(db => db.LessonTranscriptSegments.SingleAsync(x => x.LessonId == lessonId));
        Assert.Equal(text, stored.Text);
        Assert.Equal("phone,email,payment,secrecy", stored.Flags);

        using var reviewer = await Factory.LoginAsync("support");
        var reviewResponse = await reviewer.GetAsync($"/api/admin/lessons/{lessonId}/transcript");
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        var review = await reviewResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var flags = review.GetProperty("segments")[0].GetProperty("flags")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "phone", "email", "payment", "secrecy" }, flags);
    }

    [Fact]
    public async Task JoinsOnlyPreviousTextFromTheSameIdentifiedSpeaker()
    {
        var (lessonId, _) = await ParticipantsAsync();
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey,
        [
            new { speaker = "Teacher", text = "zero one five zero", atSeconds = 0.0 },
            new { speaker = "Student", text = "one two two three one", atSeconds = 1.0 },
            new { speaker = "Teacher", text = "one two two three one", atSeconds = 2.0 },
            new { speaker = "Teacher", text = "Read the page.", atSeconds = 3.0 },
        ]);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Factory.WithDbAsync(db => db.LessonTranscriptSegments
            .Where(x => x.LessonId == lessonId).OrderBy(x => x.AtSeconds).ToListAsync());
        Assert.Equal(new[] { "", "", "phone", "" }, stored.Select(x => x.Flags));
        Assert.Equal("one two two three one", stored[2].Text);
    }

    [Fact]
    public async Task DoesNotCombineUnknownSpeakers()
    {
        var (lessonId, _) = await ParticipantsAsync();
        using var request = await BuildAsync(lessonId, ApiFactory.IngestKey,
        [
            new { speaker = (string?)null, text = "zero one five zero", atSeconds = 0.0 },
            new { speaker = " ", text = "one two two three one", atSeconds = 1.0 },
            new { speaker = "Unknown", text = "zero one five zero", atSeconds = 2.0 },
        ]);

        var response = await Factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await Factory.WithDbAsync(db => db.LessonTranscriptSegments
            .AnyAsync(x => x.LessonId == lessonId && x.Flags != "")));
    }

    [Fact]
    public async Task ContextDoesNotCarryAcrossRequests()
    {
        var (lessonId, _) = await ParticipantsAsync();
        foreach (var text in new[] { "zero one five zero", "one two two three one" })
        {
            using var request = await BuildAsync(lessonId, ApiFactory.IngestKey,
                [new { speaker = "Teacher", text, atSeconds = 0.0 }]);
            Assert.Equal(HttpStatusCode.OK, (await Factory.Client.SendAsync(request)).StatusCode);
        }

        Assert.False(await Factory.WithDbAsync(db => db.LessonTranscriptSegments
            .AnyAsync(x => x.LessonId == lessonId && x.Flags != "")));
    }

    /// <summary>Creates a teacher, a student and the lesson their transcript belongs to.</summary>
    private async Task<(Guid LessonId, Guid StudentId)> ParticipantsAsync()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, student.Id);
        return (lesson.Id, student.Id);
    }

    private static async Task<HttpRequestMessage> BuildAsync(Guid lessonId, string key, List<object> segments)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/lessons/{lessonId}/transcript")
        {
            Content = JsonContent.Create(new { segments }),
        };
        request.Headers.Add("X-Transcript-Key", key);
        return request;
    }
}
