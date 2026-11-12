using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The assignment desk ranks teachers by what they can actually teach, so it
/// needs every active teacher's published week in one read.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TeacherAvailabilityBoardTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static TeacherAvailabilityRule Rule(Guid teacherId, int dayOfWeek, string starts, string ends) => new()
    {
        TeacherId = teacherId,
        DayOfWeek = dayOfWeek,
        StartsAtTime = TimeOnly.Parse(starts),
        EndsAtTime = TimeOnly.Parse(ends),
        TimeZoneId = "UTC",
    };

    private async Task<List<RuleRow>> BoardAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/api/admin/teachers/availability-rules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<RuleRow>>())!;
    }

    [Fact]
    public async Task EveryActiveTeachersPublishedWeekArrivesInOneCall()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var first = await Factory.CreateUserAsync("teacher");
        var second = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");

        var openRule = Rule(first.Id, 6, "18:00", "19:00");
        var bookedRule = Rule(first.Id, 2, "20:00", "21:00");
        var otherTeachersRule = Rule(second.Id, 0, "09:00", "10:00");
        await Factory.WithDbAsync(async db =>
        {
            db.AddRange(openRule, bookedRule, otherTeachersRule);
            db.LessonSeries.Add(new LessonSeries
            {
                TeacherId = first.Id,
                StudentId = student.Id,
                AvailabilityRuleId = bookedRule.Id,
                DayOfWeek = bookedRule.DayOfWeek,
                StartsAtTime = bookedRule.StartsAtTime,
                EndsAtTime = bookedRule.EndsAtTime,
                TimeZoneId = "UTC",
            });
            await db.SaveChangesAsync();
        });

        var board = (await BoardAsync(admin))
            .Where(x => x.TeacherId == first.Id || x.TeacherId == second.Id)
            .ToList();

        Assert.Equal(3, board.Count);
        Assert.Equal("booked", board.Single(x => x.Id == bookedRule.Id).Status);
        Assert.Equal("available", board.Single(x => x.Id == openRule.Id).Status);
        Assert.Equal("available", board.Single(x => x.Id == otherTeachersRule.Id).Status);
    }

    [Fact]
    public async Task ATeacherWithNoPublishedTimesIsSimplyAbsent()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var silent = await Factory.CreateUserAsync("teacher");

        Assert.DoesNotContain(await BoardAsync(admin), x => x.TeacherId == silent.Id);
    }

    [Fact]
    public async Task ADeactivatedTeacherDoesNotOfferTheirWeek()
    {
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var _ = admin;
        var retired = await Factory.CreateUserAsync("teacher");
        var rule = Rule(retired.Id, 4, "17:00", "18:00");
        await Factory.WithDbAsync(async db =>
        {
            db.TeacherAvailabilityRules.Add(rule);
            var user = await db.Users.SingleAsync(x => x.Id == retired.Id);
            user.IsActive = false;
            await db.SaveChangesAsync();
        });

        Assert.DoesNotContain(await BoardAsync(admin), x => x.TeacherId == retired.Id);
    }

    [Fact]
    public async Task OnlyStaffCanReadTheBoard()
    {
        var (teacher, _) = await Factory.LoginAsAsync("teacher");
        using var _ = teacher;
        var (student, _) = await Factory.LoginAsAsync("student");
        using var __ = student;

        foreach (var client in new[] { teacher, student, Factory.Client })
        {
            var response = await client.GetAsync("/api/admin/teachers/availability-rules");
            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"{response.StatusCode}");
        }
    }

    private sealed record RuleRow(
        Guid Id,
        Guid TeacherId,
        int DayOfWeek,
        string StartsAtTime,
        string EndsAtTime,
        string TimeZoneId,
        string Status);
}
