using System.Net;
using System.Text.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Cross-user object access. Every id here belongs to somebody else, so a 200
/// would mean the handler trusts the route parameter instead of the session.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class IdorTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task StrangerCannotReadALessonById()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var owner = await Factory.CreateUserAsync("student");
        var (stranger, _) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, owner.Id);

        var response = await stranger.GetAsync($"/api/lessons/{lesson.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StrangerCannotReadLessonOperations()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var (stranger, _) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, student.Id);

        var response = await stranger.GetAsync($"/api/lessons/{lesson.Id}/operations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StrangerCannotReadLessonAttendance()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var (stranger, _) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, student.Id);

        var response = await stranger.GetAsync($"/api/lessons/{lesson.Id}/attendance");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SupportStaffIsNotALessonParticipant()
    {
        // Support reviews transcripts through reviewer-only endpoints; reading a
        // lesson by id would hand over the private report and review feedback.
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var (support, _) = await Factory.LoginAsAsync("support");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, student.Id);

        var response = await support.GetAsync($"/api/lessons/{lesson.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ParticipantStillReadsTheirOwnLesson()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var (student, me) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, me.Id);

        var response = await student.GetAsync($"/api/lessons/{lesson.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LessonListNeverContainsAnotherStudentsLessons()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var otherStudent = await Factory.CreateUserAsync("student");
        var (client, me) = await Factory.LoginAsAsync("student");
        var foreign = await Factory.CreateLessonAsync(teacher.Id, otherStudent.Id);
        var mine = await Factory.CreateLessonAsync(teacher.Id, me.Id);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/lessons"));
        var ids = doc.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();

        Assert.Contains(mine.Id.ToString(), ids);
        Assert.DoesNotContain(foreign.Id.ToString(), ids);
    }

    [Fact]
    public async Task TeacherCannotReadAnUnassignedStudentProfile()
    {
        var student = await Factory.CreateUserAsync("student");
        var (teacher, _) = await Factory.LoginAsAsync("teacher");

        var response = await teacher.GetAsync($"/api/students/{student.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StudentCannotReadAnotherStudentsCreditLedger()
    {
        var student = await Factory.CreateUserAsync("student");
        var (stranger, _) = await Factory.LoginAsAsync("student");

        var response = await stranger.GetAsync($"/api/admin/students/{student.Id}/credits");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StudentCannotCancelAnotherStudentsLesson()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var owner = await Factory.CreateUserAsync("student");
        var (stranger, _) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, owner.Id);

        var response = await stranger.DeleteAsync($"/api/lessons/{lesson.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("scheduled", await Factory.WithDbAsync(db => Task.FromResult(
            db.Lessons.AsNoTracking().Single(x => x.Id == lesson.Id).Status)));
    }
}
