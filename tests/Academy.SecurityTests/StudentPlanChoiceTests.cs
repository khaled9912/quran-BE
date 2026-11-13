using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The tier a student picks is a promise about their week, so the hours they
/// offer have to be able to keep it, and the assignment desk reads both together.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class StudentPlanChoiceTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    /// <summary>An application offering <paramref name="days"/> different evenings.</summary>
    private static object Draft(bool completed, int? daysPerWeek = null, int days = 2) => new
    {
        quranLevel = "beginner",
        tajweedLevel = "beginner",
        islamicStudiesLevel = "beginner",
        arabicLevel = "beginner",
        timeZoneId = "Europe/London",
        daysPerWeek,
        weeklySlots = Enumerable.Range(0, days)
            .Select(offset => new
            {
                dayOfWeek = (6 + offset) % 7,
                startsAtTime = "19:00",
                endsAtTime = "20:00",
            })
            .ToArray(),
        completed,
    };

    private static async Task<int?> ChosenPlanAsync(HttpClient student)
    {
        // A student who has told the academy nothing gets an empty body back.
        var body = await student.GetStringAsync("/api/student/onboarding");
        return body.Length == 0 ? null : JsonNode.Parse(body)?["planDaysPerWeek"]?.GetValue<int>();
    }

    private static async Task<HttpStatusCode> ChooseAsync(HttpClient client, int daysPerWeek)
    {
        var response = await client.PatchAsJsonAsync("/api/student/plan", new { daysPerWeek });
        return response.StatusCode;
    }

    [Fact]
    public async Task AStudentRecordsThePlanTheyWant()
    {
        var (student, user) = await Factory.LoginAsAsync("student");
        using var _ = student;
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var __ = admin;

        Assert.Equal(HttpStatusCode.OK, await ChooseAsync(student, 3));
        Assert.Equal(3, await ChosenPlanAsync(student));

        var queue = (await admin.GetFromJsonAsync<JsonNode>("/api/admin/students/pending"))!.AsArray();
        var row = queue.Single(x => x!["studentId"]!.GetValue<Guid>() == user.Id)!;
        Assert.Equal(3, row["onboarding"]!["planDaysPerWeek"]!.GetValue<int>());
    }

    [Fact]
    public async Task ChangingTheirPlanReplacesIt()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        await ChooseAsync(student, 2);
        Assert.Equal(HttpStatusCode.OK, await ChooseAsync(student, 5));
        Assert.Equal(5, await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task APlanTheAcademyDoesNotSellIsRefused()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        foreach (var days in new[] { 0, 1, 6, 7, -1 })
            Assert.Equal(HttpStatusCode.BadRequest, await ChooseAsync(student, days));

        Assert.Null(await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task TheWizardIsWhereANewAccountStatesTheirPlan()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        var saved = await student.PutAsJsonAsync("/api/student/onboarding", Draft(true, daysPerWeek: 4, days: 4));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(4, await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task AFinishedApplicationMustSayHowOftenTheStudentWantsToLearn()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        var saved = await student.PutAsJsonAsync("/api/student/onboarding", Draft(true));

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Null(await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task ATierTheOfferedDaysCannotHoldIsRefused()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        var saved = await student.PutAsJsonAsync("/api/student/onboarding", Draft(true, daysPerWeek: 5, days: 3));

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Null(await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task BackingOutOfTheWizardMayStillLeaveThePlanUnanswered()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        var saved = await student.PutAsJsonAsync("/api/student/onboarding", Draft(false));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(0, await ChosenPlanAsync(student));
    }

    [Fact]
    public async Task APlanCannotOutgrowTheTimesAlreadyOffered()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        var saved = await student.PutAsJsonAsync("/api/student/onboarding", Draft(true, daysPerWeek: 2));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, await ChooseAsync(student, 4));
        Assert.Equal(2, await ChosenPlanAsync(student));
        Assert.Equal(HttpStatusCode.OK, await ChooseAsync(student, 2));
    }

    [Fact]
    public async Task TheDeskSeesWhichHoursAlreadyHaveALesson()
    {
        var (student, user) = await Factory.LoginAsAsync("student");
        using var _ = student;
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var __ = admin;
        var teacher = await Factory.CreateUserAsync("teacher");

        Assert.Equal(
            HttpStatusCode.OK,
            (await student.PutAsJsonAsync("/api/student/onboarding", Draft(true, daysPerWeek: 2))).StatusCode);
        await Factory.WithDbAsync(async db =>
        {
            var rule = new TeacherAvailabilityRule
            {
                TeacherId = teacher.Id,
                DayOfWeek = 6,
                StartsAtTime = TimeOnly.Parse("19:00"),
                EndsAtTime = TimeOnly.Parse("19:30"),
                TimeZoneId = "Europe/London",
            };
            db.LessonSeries.Add(new LessonSeries
            {
                TeacherId = teacher.Id,
                StudentId = user.Id,
                AvailabilityRuleId = rule.Id,
                DayOfWeek = rule.DayOfWeek,
                StartsAtTime = rule.StartsAtTime,
                EndsAtTime = rule.EndsAtTime,
                TimeZoneId = rule.TimeZoneId,
            });
            db.TeacherAvailabilityRules.Add(rule);
            await db.SaveChangesAsync();
        });

        var queue = (await admin.GetFromJsonAsync<JsonNode>("/api/admin/students/pending"))!.AsArray();
        var row = queue.Single(x => x!["studentId"]!.GetValue<Guid>() == user.Id)!;

        var lesson = Assert.Single(row["weeklyLessons"]!.AsArray());
        Assert.Equal(6, lesson!["dayOfWeek"]!.GetValue<int>());
        Assert.Equal("19:00", lesson["startsAtTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyTheStudentChoosesTheirOwnPlan()
    {
        var (teacher, _) = await Factory.LoginAsAsync("teacher");
        using var _ = teacher;
        var (admin, _) = await Factory.LoginAsAsync("admin");
        using var __ = admin;

        foreach (var client in new[] { teacher, admin, Factory.Client })
        {
            var status = await ChooseAsync(client, 3);
            Assert.True(
                status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"{status}");
        }
    }
}
