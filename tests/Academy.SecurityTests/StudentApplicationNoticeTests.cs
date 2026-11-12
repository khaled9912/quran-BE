using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class StudentApplicationNoticeTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static object Draft(bool completed) => new
    {
        quranLevel = "beginner",
        tajweedLevel = "intermediate",
        islamicStudiesLevel = "advanced",
        arabicLevel = "beginner",
        timeZoneId = "Europe/London",
        weeklySlots = new object[]
        {
            new { dayOfWeek = 6, startsAtTime = "19:00", endsAtTime = "20:00" },
            new { dayOfWeek = 0, startsAtTime = "20:00", endsAtTime = "21:00" },
        },
        completed,
    };

    private Task<List<NoticeRow>> NoticesAsync() => Factory.WithDbAsync(db => db.Notifications
        .AsNoTracking()
        .Where(x => x.DedupeKey != null && x.DedupeKey.StartsWith("student-waiting:"))
        .Select(x => new NoticeRow(x.UserId, x.Title, x.Kind, x.Link, x.Body))
        .ToListAsync());

    [Fact]
    public async Task FinishingSetupTellsEveryAdminOnceWithTheApplicationInHand()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;
        var admin = await Factory.CreateUserAsync("admin");
        var second = await Factory.CreateUserAsync("admin");
        await Factory.CreateUserAsync("teacher");

        using var response = await student.PutAsJsonAsync("/api/student/onboarding", Draft(true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var notices = await NoticesAsync();
        Assert.Equal(2, notices.Count);
        Assert.Contains(admin.Id, notices.Select(x => x.UserId));
        Assert.Contains(second.Id, notices.Select(x => x.UserId));
        Assert.All(notices, notice =>
        {
            Assert.Equal("Student waiting for a teacher", notice.Title);
            Assert.Equal("request", notice.Kind);
            Assert.Equal("/people/assign", notice.Link);
            Assert.Contains("Test student", notice.Body);
            Assert.Contains("Arabic Beginner", notice.Body);
            Assert.Contains("London time", notice.Body);
            Assert.Contains("and 1 more", notice.Body);
        });
    }

    [Fact]
    public async Task BackingOutHalfwayAnnouncesNobody()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;
        await Factory.CreateUserAsync("admin");

        using var response = await student.PutAsJsonAsync("/api/student/onboarding", Draft(false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task UpdatingTheApplicationTellsTheDeskAgain()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;
        await Factory.CreateUserAsync("admin");

        Assert.Equal(HttpStatusCode.OK, (await student.PutAsJsonAsync("/api/student/onboarding", Draft(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.PutAsJsonAsync("/api/student/onboarding", Draft(true))).StatusCode);

        var notices = await NoticesAsync();
        Assert.Equal(2, notices.Count);
        Assert.Single(notices, x => x.Title == "Student waiting for a teacher");
        var update = Assert.Single(notices, x => x.Title == "Student updated their application");
        Assert.Contains("still waiting for a teacher", update.Body);
        Assert.Contains("Arabic Beginner", update.Body);
    }

    [Fact]
    public async Task AStudentWhoAlreadyHasATeacherIsNotWaitingOnAnyone()
    {
        var (student, user) = await Factory.LoginAsAsync("student");
        using var _ = student;
        var admin = await Factory.CreateUserAsync("admin");
        var teacher = await Factory.CreateUserAsync("teacher");
        await Factory.WithDbAsync(async db =>
        {
            db.Assignments.Add(new TeacherStudentAssignment { TeacherId = teacher.Id, StudentId = user.Id });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.OK, (await student.PutAsJsonAsync("/api/student/onboarding", Draft(true))).StatusCode);
        Assert.Empty(await NoticesAsync());
        Assert.Equal(0, await Factory.WithDbAsync(db => db.Notifications.CountAsync(x => x.UserId == admin.Id)));
    }

    private sealed record NoticeRow(Guid UserId, string Title, string Kind, string? Link, string Body);
}
