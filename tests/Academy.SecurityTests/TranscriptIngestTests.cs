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
