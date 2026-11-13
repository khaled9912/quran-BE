using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// A student may only move a weekly lesson onto a time that leaves the teacher
/// the academy's notice window. Times whose next class falls inside it must be
/// reported as too near, so the clients can drop them from the list, and a move
/// onto one must be refused even if a client offers it anyway.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TimeChangeLeadTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const int LeadMinutes = 60;

    private sealed record Scenario(Guid StudentId, Guid HomeRuleId, Guid SeriesId, Guid SoonRuleId, Guid FarRuleId);

    [Fact]
    public async Task WeeklyTimesCarryTheirNextOccurrenceAndTheAcademyNoticeWindow()
    {
        var (client, scenario) = await SeedAsync();
        var read = await GetWeeklyAvailability(client, scenario);
        var slots = read.GetProperty("slots").EnumerateArray()
            .ToDictionary(x => Guid.Parse(x.GetProperty("id").GetString()!), x => x);

        Assert.Equal(LeadMinutes, read.GetProperty("timeChangeLeadMinutes").GetInt32());
        Assert.True(NoticeFor(slots[scenario.SoonRuleId]) < TimeSpan.FromMinutes(LeadMinutes));
        Assert.True(NoticeFor(slots[scenario.FarRuleId]) >= TimeSpan.FromMinutes(LeadMinutes));
    }

    [Fact]
    public async Task MovingOntoATimeInsideTheNoticeWindowIsRefused()
    {
        var (client, scenario) = await SeedAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/student/series/{scenario.SeriesId}/change-requests",
            new { requestedRuleId = scenario.SoonRuleId, note = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("notice", await ReadMessageAsync(response));
        Assert.Equal(scenario.HomeRuleId, await BookedRuleAsync(scenario.SeriesId));
    }

    [Fact]
    public async Task MovingOntoATimeOutsideTheNoticeWindowIsApplied()
    {
        var (client, scenario) = await SeedAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/student/series/{scenario.SeriesId}/change-requests",
            new { requestedRuleId = scenario.FarRuleId, note = (string?)null });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(scenario.FarRuleId, await BookedRuleAsync(scenario.SeriesId));
    }

    /// <summary>A student whose current weekly time is <paramref name="home"/> and a teacher who publishes it.</summary>
    private async Task<(HttpClient Client, Scenario Data)> SeedAsync()
    {
        var student = await Factory.CreateUserAsync("student");
        var teacher = await Factory.CreateUserAsync("teacher");
        var client = await Factory.SignInAsync(student.Email);

        var home = Rule(teacher.Id, DateTimeOffset.UtcNow.AddDays(2), "current");
        var soon = Rule(teacher.Id, OffsetInsideLead(), "soon");
        var far = Rule(teacher.Id, DateTimeOffset.UtcNow.AddHours(6), "far");
        await Factory.WithDbAsync(async db =>
        {
            db.TeacherAvailabilityRules.AddRange(home, soon, far);
            // A student only ever reads their own teacher's week, and that
            // visibility comes from the assignment the academy made.
            db.Assignments.Add(new TeacherStudentAssignment { TeacherId = teacher.Id, StudentId = student.Id });
            db.LessonSeries.Add(new LessonSeries
            {
                TeacherId = teacher.Id,
                StudentId = student.Id,
                AvailabilityRuleId = home.Id,
                DayOfWeek = home.DayOfWeek,
                StartsAtTime = home.StartsAtTime,
                EndsAtTime = home.EndsAtTime,
                TimeZoneId = home.TimeZoneId,
            });
            await db.SaveChangesAsync();
        });

        return (client, new Scenario(student.Id, home.Id, await SeriesIdAsync(student.Id), soon.Id, far.Id));
    }

    /// <summary>
    /// The next class a student might move onto while still inside the window.
    /// Snapping to a quarter hour keeps the whole lesson on one calendar day,
    /// which a weekly time cannot leave.
    /// </summary>
    private static DateTimeOffset OffsetInsideLead()
    {
        var soon = DateTimeOffset.UtcNow.AddMinutes(30);
        return new DateTimeOffset(soon.Year, soon.Month, soon.Day, soon.Hour, soon.Minute / 15 * 15, 0, TimeSpan.Zero);
    }

    private static TeacherAvailabilityRule Rule(Guid teacherId, DateTimeOffset sample, string label) => new()
    {
        TeacherId = teacherId,
        DayOfWeek = (int)sample.DayOfWeek,
        StartsAtTime = TimeOnly.FromDateTime(sample.UtcDateTime),
        EndsAtTime = TimeOnly.FromDateTime(sample.UtcDateTime.AddMinutes(10)),
        TimeZoneId = "UTC",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static TimeSpan NoticeFor(JsonElement slot) =>
        slot.GetProperty("nextStartsAt").GetDateTime() - DateTimeOffset.UtcNow;

    private async Task<JsonElement> GetWeeklyAvailability(HttpClient client, Scenario scenario)
    {
        var teacherId = await Factory.WithDbAsync(db => db.LessonSeries
            .Where(x => x.Id == scenario.SeriesId).Select(x => x.TeacherId).SingleAsync());
        var response = await client.GetAsync($"/api/teachers/{teacherId}/weekly-availability");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private Task<Guid> SeriesIdAsync(Guid studentId) => Factory.WithDbAsync(db => db.LessonSeries
        .Where(x => x.StudentId == studentId).Select(x => x.Id).SingleAsync());

    private Task<Guid> BookedRuleAsync(Guid seriesId) => Factory.WithDbAsync(db => db.LessonSeries
        .Where(x => x.Id == seriesId).Select(x => x.AvailabilityRuleId).SingleAsync());

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.GetProperty("message").GetString() ?? "";
    }
}
