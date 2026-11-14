using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Nobody starts working at the academy before their agreement is on file. The
/// refusal is the API's, not a banner's: the account's first real action comes
/// back as 428 until the signed clause list for the current version is stored.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PolicyAcceptanceTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static async Task<JsonObject> BodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    /// <summary>
    /// What the server renders today. Tests sign against this rather than the
    /// C# template because the numbers in the text come from the live rule row,
    /// and a client only ever sees the filled document.
    /// </summary>
    private async Task<(int Version, string[] ClauseIds, string Text)> DocumentAsync(string key)
    {
        var document = JsonNode.Parse(await Factory.RawClient.GetStringAsync($"/api/policy/{key}"))!.AsObject();
        var clauses = document["clauses"]!.AsArray();
        string[] ids = [.. clauses.Select(x => x!["id"]!.GetValue<string>())];
        var text = string.Join(" ", clauses.SelectMany(x => new[] { x!["title"]!.GetValue<string>(), x!["body"]!.GetValue<string>() }));
        return (document["version"]!.GetValue<int>(), ids, text);
    }

    private Task SetStudentFreeCancelHoursAsync(int hours) => Factory.WithDbAsync(async db =>
    {
        (await LessonPolicyService.GetPolicyAsync(db)).StudentFreeCancelHours = hours;
        await db.SaveChangesAsync();
    });

    private static Task<HttpResponseMessage> SignAsync(HttpClient client, string key, int version, IEnumerable<string> clauseIds) =>
        client.PostAsJsonAsync("/api/policy/accept", new { policyKey = key, version, clauseIds = clauseIds.ToArray() });

    private static async Task<bool> AcceptedAsync(HttpClient client)
    {
        var state = await client.GetFromJsonAsync<JsonObject>("/api/policy/state");
        return state!["accepted"]!.GetValue<bool>();
    }

    private static Task<HttpResponseMessage> AnnounceAsync(HttpClient teacher) =>
        teacher.PostAsJsonAsync("/api/teacher/availability-rules",
            new { daysOfWeek = new[] { 1, 3, 5 }, startsAtTime = "17:00", endsAtTime = "17:30", timeZoneId = "UTC" });

    private static Task<HttpResponseMessage> FinishWizardAsync(HttpClient student, bool completed) =>
        student.PutAsJsonAsync("/api/student/onboarding", new
        {
            quranLevel = "beginner",
            tajweedLevel = "beginner",
            islamicStudiesLevel = "beginner",
            arabicLevel = "beginner",
            timeZoneId = "Europe/London",
            daysPerWeek = 2,
            weeklySlots = new[]
            {
                new { dayOfWeek = 1, startsAtTime = "19:00", endsAtTime = "19:30" },
                new { dayOfWeek = 3, startsAtTime = "19:00", endsAtTime = "19:30" },
            },
            completed,
        });

    private static Task<HttpResponseMessage> BeatAsync(HttpClient participant, Guid lessonId, string action) =>
        participant.PostAsJsonAsync($"/api/lessons/{lessonId}/attendance/events",
            new { action, eventId = Guid.NewGuid().ToString(), deviceId = "test-device" });

    [Fact]
    public async Task AnUnsignedTeacherCannotAnnounceTeachingTimes()
    {
        using var teacher = await Factory.LoginAsync("teacher", signed: false);

        var refusal = await AnnounceAsync(teacher);
        Assert.Equal(HttpStatusCode.PreconditionRequired, refusal.StatusCode);
        var body = await BodyAsync(refusal);
        Assert.Equal("policy_acceptance_required", body["code"]!.GetValue<string>());
        Assert.Equal(PolicyKeys.Teacher, body["policyKey"]!.GetValue<string>());

        // Signing the current version is what unlocks it, not a second attempt.
        var (version, clauses, _) = await DocumentAsync(PolicyKeys.Teacher);
        var signed = await SignAsync(teacher, PolicyKeys.Teacher, version, clauses);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.True(await AcceptedAsync(teacher));
        Assert.Equal(HttpStatusCode.Created, (await AnnounceAsync(teacher)).StatusCode);
    }

    [Fact]
    public async Task AnUnsignedStudentCanAnswerTheWizardButNotSubmitIt()
    {
        using var student = await Factory.LoginAsync("student", signed: false);

        // Backing out mid-wizard is allowed; only the finished application is a
        // real action, so only it is refused.
        Assert.Equal(HttpStatusCode.OK, (await FinishWizardAsync(student, false)).StatusCode);

        var refusal = await FinishWizardAsync(student, true);
        Assert.Equal(HttpStatusCode.PreconditionRequired, refusal.StatusCode);
        Assert.Equal(PolicyKeys.Parent, (await BodyAsync(refusal))["policyKey"]!.GetValue<string>());

        var family = await DocumentAsync(PolicyKeys.Parent);
        Assert.Equal(HttpStatusCode.OK, (await SignAsync(student, PolicyKeys.Parent, family.Version, family.ClauseIds)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await FinishWizardAsync(student, true)).StatusCode);
    }

    [Fact]
    public async Task TheRoomRefusesAnUnsignedParticipantAndNeverBlocksLeaving()
    {
        var (teacher, teacherUser) = await Factory.LoginAsAsync("teacher");
        using var _ = teacher;
        var (student, studentUser) = await Factory.LoginAsAsync("student", signed: false);
        using var __ = student;
        var lesson = await Factory.CreateLessonAsync(teacherUser.Id, studentUser.Id);

        // The signed teacher is in; the unsigned student is not.
        Assert.Equal(HttpStatusCode.OK, (await BeatAsync(teacher, lesson.Id, "join")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await BeatAsync(student, lesson.Id, "join")).StatusCode);

        // Leaving is never refused, so a participant cannot be trapped in a room
        // whose attendance is already open for them.
        var leave = await BeatAsync(student, lesson.Id, "leave");
        Assert.NotEqual(HttpStatusCode.PreconditionRequired, leave.StatusCode);

        var family = await DocumentAsync(PolicyKeys.Parent);
        Assert.Equal(HttpStatusCode.OK, (await SignAsync(student, PolicyKeys.Parent, family.Version, family.ClauseIds)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await BeatAsync(student, lesson.Id, "join")).StatusCode);
    }

    [Fact]
    public async Task APartialOrForeignSignatureIsNotASignature()
    {
        using var teacher = await Factory.LoginAsync("teacher", signed: false);
        var (version, clauses, _) = await DocumentAsync(PolicyKeys.Teacher);

        var partial = await SignAsync(teacher, PolicyKeys.Teacher, version, clauses[..^1]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
        Assert.NotNull((await BodyAsync(partial))["missing"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SignAsync(teacher, PolicyKeys.Teacher, version, [.. clauses, "not-a-clause"])).StatusCode);

        // A client holding an older build must not sign text it never saw.
        Assert.Equal(HttpStatusCode.Conflict,
            (await SignAsync(teacher, PolicyKeys.Teacher, version + 1, clauses)).StatusCode);

        // The family agreement is not the teacher's document.
        var family = await DocumentAsync(PolicyKeys.Parent);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SignAsync(teacher, PolicyKeys.Parent, family.Version, family.ClauseIds)).StatusCode);

        Assert.False(await AcceptedAsync(teacher));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await AnnounceAsync(teacher)).StatusCode);
    }

    [Fact]
    public async Task SigningTwiceKeepsTheFirstSignature()
    {
        var (teacher, user) = await Factory.LoginAsAsync("teacher", signed: false);
        using var _ = teacher;
        var (version, clauses, _) = await DocumentAsync(PolicyKeys.Teacher);

        var first = await BodyAsync(await SignAsync(teacher, PolicyKeys.Teacher, version, clauses));
        var replay = await BodyAsync(await SignAsync(teacher, PolicyKeys.Teacher, version, clauses));

        Assert.Equal(first["acceptedAt"]!.GetValue<DateTimeOffset>(), replay["acceptedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(1, await Factory.WithDbAsync(db => db.PolicyAcceptances.CountAsync(x => x.UserId == user.Id)));
    }

    [Fact]
    public async Task StaffHaveNoDocumentToSignAndAnyoneCanReadTheText()
    {
        using var admin = await Factory.LoginAsync("admin");
        var state = await admin.GetFromJsonAsync<JsonObject>("/api/policy/state");
        Assert.False(state!["required"]!.GetValue<bool>());

        // A prospective teacher reads the rules before applying, from the browser.
        using var anonymous = Factory.RawClient;
        var document = JsonNode.Parse(await anonymous.GetStringAsync("/api/policy/teacher"))!.AsObject();
        Assert.Equal("ar", document["language"]!.GetValue<string>());
        Assert.Equal(9, document["clauses"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/policy/nonsense")).StatusCode);

        // The pending list is session data.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/policy/state")).StatusCode);
    }

    [Fact]
    public async Task TheRuleNumbersInTheTextComeFromTheLiveRuleRow()
    {
        // The templates hold tokens, so what a signer is shown is always the
        // filled document. A sentence with an unfilled token in it would be a
        // promise the engine does not make.
        foreach (var key in new[] { PolicyKeys.Teacher, PolicyKeys.Parent })
            Assert.DoesNotContain("{", (await DocumentAsync(key)).Text);

        var policy = await Factory.CurrentPolicyAsync();
        Assert.Contains(
            $"{policy.StudentFreeCancelHours} hours",
            (await DocumentAsync(PolicyKeys.Parent)).Text);
    }

    [Fact]
    public async Task ANewRuleNumberRedrawsTheTextAndReasksWithoutADeploy()
    {
        var old = await DocumentAsync(PolicyKeys.Parent);
        using var student = await Factory.LoginAsync("student", signed: true);
        Assert.True(await AcceptedAsync(student));

        await SetStudentFreeCancelHoursAsync(8);
        try
        {
            var (version, clauses, text) = await DocumentAsync(PolicyKeys.Parent);
            Assert.NotEqual(old.Version, version);
            Assert.Contains("8 hours", text);

            // The signature the family gave the old wording does not cover the
            // new one: the refusal is the API's again.
            Assert.False(await AcceptedAsync(student));
            Assert.Equal(HttpStatusCode.PreconditionRequired, (await FinishWizardAsync(student, true)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await SignAsync(student, PolicyKeys.Parent, version, clauses)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await FinishWizardAsync(student, true)).StatusCode);
        }
        finally
        {
            await SetStudentFreeCancelHoursAsync(12);
        }
    }
}
