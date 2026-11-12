using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Academy.SecurityTests.Infrastructure;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// A student picking a tier on the public plans page leaves a record the
/// assignment desk reads next to their weekly hours. Nothing is billed from it.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class StudentPlanChoiceTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
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
    public async Task FinishingTheSetupWizardKeepsThePlanTheyChose()
    {
        var (student, _) = await Factory.LoginAsAsync("student");
        using var _ = student;

        await ChooseAsync(student, 4);
        var saved = await student.PutAsJsonAsync(
            "/api/student/onboarding",
            new
            {
                quranLevel = "beginner",
                tajweedLevel = "beginner",
                islamicStudiesLevel = "beginner",
                arabicLevel = "beginner",
                timeZoneId = "UTC",
                weeklySlots = new[] { new { dayOfWeek = 6, startsAtTime = "18:00", endsAtTime = "18:30" } },
                completed = true,
            });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(4, await ChosenPlanAsync(student));
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
