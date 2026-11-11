using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class ConductConcernTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Concern = "Private concern: the teacher asked me to contact them outside the academy.";
    private const string ReviewNote = "Private review note: discussed the concern with the reporter.";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task MissingOrDisabledFlagReturns404WithoutDisablingAttendanceReports(string? flag)
    {
        SetFlag(flag);
        var (student, user) = await Factory.LoginAsAsync("student");
        using var client = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, user.Id);

        using var response = await ReportAsync(client, lesson.Id, Concern);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonIncidents.CountAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.Notifications.CountAsync(x => x.Title == "Lesson conduct concern")));

        using var attendance = await client.PostAsJsonAsync($"/api/lessons/{lesson.Id}/incident", new
        {
            type = "attendance_dispute", detail = "The attendance log needs review.",
        });
        Assert.Equal(HttpStatusCode.OK, attendance.StatusCode);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1000)]
    public async Task StudentCanSubmitAtLengthBoundariesAndCannotDuplicate(int length)
    {
        SetFlag("true");
        var (student, user) = await Factory.LoginAsAsync("student");
        using var client = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        await Factory.CreateUserAsync("admin");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, user.Id);
        var detail = new string('x', length);

        using var response = await client.PostAsJsonAsync($"/api/lessons/{lesson.Id}/incident", new
        {
            type = " CONDUCT_CONCERN ", detail = $"  {detail}  ",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<LessonIncidentDto>();
        Assert.NotNull(report);
        Assert.Equal("conduct_concern", report.Type);
        Assert.Equal("open", report.Status);
        Assert.Equal(user.Id, report.ReportedBy);
        Assert.Equal(detail, report.Detail);

        using var duplicate = await ReportAsync(client, lesson.Id, "A second report is not allowed.");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonIncidents.CountAsync(x => x.LessonId == lesson.Id)));
        Assert.Equal(1, await Factory.WithDbAsync(db => db.Notifications.CountAsync(x => x.Title == "Lesson conduct concern")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1001)]
    public async Task InvalidTrimmedLengthsDoNotPersistOrNotify(int? length)
    {
        SetFlag("true");
        var (student, user) = await Factory.LoginAsAsync("student");
        using var client = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, user.Id);
        var detail = length is null ? null : $"  {new string('x', length.Value)}  ";

        using var response = await ReportAsync(client, lesson.Id, detail);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("detail", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonIncidents.CountAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.Notifications.CountAsync(x => x.Title == "Lesson conduct concern")));
    }

    [Theory]
    [InlineData("teacher", "teacher")]
    [InlineData("student", "unrelated")]
    [InlineData("admin", "unrelated")]
    [InlineData("support", "unrelated")]
    [InlineData("teacher", "student")]
    [InlineData("admin", "student")]
    [InlineData("support", "student")]
    public async Task OwnershipAndStudentRoleAreBothRequired(string role, string position)
    {
        SetFlag("true");
        var (actor, user) = await Factory.LoginAsAsync(role);
        using var client = actor;
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var lesson = await Factory.CreateLessonAsync(
            position == "teacher" ? user.Id : teacher.Id,
            position == "student" ? user.Id : student.Id);

        using var response = await ReportAsync(client, lesson.Id, Concern);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonIncidents.CountAsync()));
    }

    [Fact]
    public async Task ReportingRequiresAuthenticationAndAnExistingLesson()
    {
        SetFlag("true");
        using var anonymous = Factory.Client;
        using var unauthorized = await ReportAsync(anonymous, Guid.NewGuid(), Concern);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var student = await Factory.LoginAsync("student");
        using var missing = await ReportAsync(student, Guid.NewGuid(), Concern);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task OperationsHideConductFromTeacherButKeepReportersAndExistingStaffAccess()
    {
        SetFlag("true");
        var (student, studentUser) = await Factory.LoginAsAsync("student");
        var (teacher, teacherUser) = await Factory.LoginAsAsync("teacher");
        using var studentClient = student;
        using var teacherClient = teacher;
        using var admin = await Factory.LoginAsync("admin");
        using var support = await Factory.LoginAsync("support");
        using var stranger = await Factory.LoginAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacherUser.Id, studentUser.Id);
        using var attendance = await teacherClient.PostAsJsonAsync($"/api/lessons/{lesson.Id}/incident", new
        {
            type = "attendance_dispute", detail = "Ordinary attendance dispute remains visible.",
        });
        attendance.EnsureSuccessStatusCode();
        using var reported = await ReportAsync(studentClient, lesson.Id, Concern);
        reported.EnsureSuccessStatusCode();

        // Disabling new reports must not remove access to existing private history.
        SetFlag("false");
        foreach (var reader in new[] { studentClient, admin, support })
        {
            var operations = await GetJsonAsync(reader, $"/api/lessons/{lesson.Id}/operations");
            var concern = Assert.Single(operations.GetProperty("incidents").EnumerateArray(),
                x => x.GetProperty("type").GetString() == "conduct_concern");
            Assert.Equal(Concern, concern.GetProperty("detail").GetString());
        }
        var teacherOperations = await GetJsonAsync(teacherClient, $"/api/lessons/{lesson.Id}/operations");
        Assert.DoesNotContain(Concern, teacherOperations.GetRawText());
        Assert.DoesNotContain("conduct_concern", teacherOperations.GetRawText());
        Assert.Equal("attendance_dispute", Assert.Single(teacherOperations.GetProperty("incidents").EnumerateArray())
            .GetProperty("type").GetString());
        using var denied = await stranger.GetAsync($"/api/lessons/{lesson.Id}/operations");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        foreach (var path in new[]
        {
            "/api/admin/lesson-incidents?status=open",
            $"/api/admin/lesson-ops/{lesson.Id}",
            $"/api/admin/lesson-ops?date={LessonPolicyService.DayBounds(null, lesson.StartsAt).Key}",
        })
        {
            Assert.Contains(Concern, (await GetJsonAsync(admin, path)).GetRawText());
            foreach (var reader in new[] { teacherClient, studentClient, support })
            {
                using var forbidden = await reader.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
        }
    }

    [Fact]
    public async Task AReplacementStudentCannotReadThePreviousStudentsReport()
    {
        SetFlag("true");
        var (student, studentUser) = await Factory.LoginAsAsync("student");
        using var studentClient = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, studentUser.Id);
        using var reported = await ReportAsync(studentClient, lesson.Id, Concern);
        reported.EnsureSuccessStatusCode();
        var (replacement, replacementUser) = await Factory.LoginAsAsync("student");
        using var replacementClient = replacement;
        await Factory.WithDbAsync(async db =>
        {
            (await db.Lessons.SingleAsync(x => x.Id == lesson.Id)).StudentId = replacementUser.Id;
            await db.SaveChangesAsync();
        });

        var operations = await GetJsonAsync(replacementClient, $"/api/lessons/{lesson.Id}/operations");
        Assert.Empty(operations.GetProperty("incidents").EnumerateArray());
        Assert.DoesNotContain(Concern, operations.GetRawText());
    }

    [Fact]
    public async Task NotificationsAndPushGoOnlyToAdminsWithoutConfidentialText()
    {
        SetFlag("true");
        var capture = new PushCapture();
        // Replace only this isolated host's Expo transport; never contact a provider.
        Factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("expo")
            .HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = capture);
        var (student, studentUser) = await Factory.LoginAsAsync("student");
        using var client = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        var support = await Factory.CreateUserAsync("support");
        var admin = await Factory.CreateUserAsync("admin");
        var secondAdmin = await Factory.CreateUserAsync("admin");
        // A teacher with an additional admin role must still never receive the report.
        await AddRoleAsync(teacher, "admin");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, studentUser.Id);
        await Factory.WithDbAsync(async db =>
        {
            foreach (var user in new[] { studentUser, teacher, support, admin, secondAdmin })
                db.DeviceTokens.Add(new DeviceToken { UserId = user.Id, Token = $"ExpoPushToken[{user.Id}]" });
            await db.SaveChangesAsync();
        });

        using var response = await ReportAsync(client, lesson.Id, Concern);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var incident = (await response.Content.ReadFromJsonAsync<LessonIncidentDto>())!;
        var notices = await Factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(x => x.DedupeKey != null && x.DedupeKey.StartsWith($"incident-admin:{incident.Id}:"))
            .ToListAsync());
        Assert.Equal(2, notices.Count);
        Assert.Equal(new[] { admin.Id, secondAdmin.Id }.Order(), notices.Select(x => x.UserId).Order());
        Assert.All(notices, notice =>
        {
            Assert.Equal("Lesson conduct concern", notice.Title);
            Assert.Equal($"/lesson/{lesson.Id}", notice.Link);
            Assert.DoesNotContain(Concern, notice.Body);
        });
        var pushes = capture.Messages.Where(x => x.GetProperty("title").GetString() == "Lesson conduct concern").ToList();
        Assert.Equal(2, pushes.Count);
        Assert.Equal(new[] { $"ExpoPushToken[{admin.Id}]", $"ExpoPushToken[{secondAdmin.Id}]" }.Order(),
            pushes.Select(x => x.GetProperty("to").GetString()!).Order());
        Assert.All(capture.Messages, message => Assert.DoesNotContain(Concern, message.GetRawText()));

        using var teacherClient = await Factory.SignInAsync(teacher.Email);
        foreach (var path in new[]
        {
            $"/api/lessons/{lesson.Id}/operations", "/api/admin/lesson-incidents",
            $"/api/admin/lesson-ops/{lesson.Id}", $"/api/admin/lesson-ops?date={LessonPolicyService.DayBounds(null, lesson.StartsAt).Key}",
        })
            Assert.DoesNotContain("conduct_concern", (await GetJsonAsync(teacherClient, path)).GetRawText());
        using var resolution = await teacherClient.PostAsJsonAsync($"/api/admin/lesson-incidents/{incident.Id}/resolve",
            new { action = "dismissed", note = ReviewNote });
        Assert.Equal(HttpStatusCode.Forbidden, resolution.StatusCode);
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("dismissed")]
    public async Task ResolutionStaysPrivateAndNeverSettlesAttendanceOrPenalties(string action)
    {
        SetFlag("true");
        var (student, studentUser) = await Factory.LoginAsAsync("student");
        var (teacher, teacherUser) = await Factory.LoginAsAsync("teacher");
        using var studentClient = student;
        using var teacherClient = teacher;
        using var admin = await Factory.LoginAsync("admin");
        using var support = await Factory.LoginAsync("support");
        var lesson = await Factory.CreateLessonAsync(teacherUser.Id, studentUser.Id);
        using var reported = await ReportAsync(studentClient, lesson.Id, Concern);
        reported.EnsureSuccessStatusCode();
        var incident = (await reported.Content.ReadFromJsonAsync<LessonIncidentDto>())!;
        foreach (var reader in new[] { studentClient, teacherClient, support })
        {
            using var denied = await reader.PostAsJsonAsync($"/api/admin/lesson-incidents/{incident.Id}/resolve",
                new { action, note = ReviewNote });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incident.Id}/resolve",
            new { action, note = ReviewNote, teacherPayableMinutes = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = (await response.Content.ReadFromJsonAsync<LessonIncidentDto>())!;
        Assert.Equal(action, resolved.Status);
        Assert.Equal(ReviewNote, resolved.ResolutionNote);
        Assert.NotNull(resolved.ResolvedAt);
        using var duplicate = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incident.Id}/resolve",
            new { action, note = ReviewNote });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        await Factory.WithDbAsync(async db =>
        {
            var unchanged = await db.Lessons.AsNoTracking().SingleAsync(x => x.Id == lesson.Id);
            Assert.Equal("scheduled", unchanged.Status);
            Assert.Null(unchanged.CancelReason);
            Assert.Equal(0, await db.LessonCancellations.CountAsync(x => x.LessonId == lesson.Id));
            Assert.Equal(0, await db.LessonCreditTransactions.CountAsync(x => x.LessonId == lesson.Id));
            Assert.Equal(0, await db.ReliabilityEvents.CountAsync(x => x.LessonId == lesson.Id));
            Assert.Equal(0, await db.TeacherPenalties.CountAsync(x => x.LessonId == lesson.Id));
            Assert.Equal(0, await db.TeacherHours.CountAsync(x => x.LessonId == lesson.Id));
            Assert.Equal(0, await db.Notifications.CountAsync(x =>
                x.DedupeKey == $"incident-student:{incident.Id}" || x.DedupeKey == $"incident-teacher:{incident.Id}"));
            Assert.Equal(0, await db.Notifications.CountAsync(x => x.Body.Contains(Concern) || x.Body.Contains(ReviewNote)));
            Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.Detail.Contains(Concern) || x.Detail.Contains(ReviewNote)));
        });
        var own = await GetJsonAsync(studentClient, $"/api/lessons/{lesson.Id}/operations");
        Assert.Contains(ReviewNote, own.GetRawText());
        var hidden = await GetJsonAsync(teacherClient, $"/api/lessons/{lesson.Id}/operations");
        Assert.DoesNotContain(Concern, hidden.GetRawText());
        Assert.DoesNotContain(ReviewNote, hidden.GetRawText());
        Assert.DoesNotContain("conduct_concern", hidden.GetRawText());
    }

    [Theory]
    [InlineData("teacher_no_show")]
    [InlineData("student_no_show")]
    [InlineData("both_absent")]
    [InlineData("misconduct")]
    public async Task ConductCannotBeResolvedAsAttendanceOrAutomaticMisconductPenalty(string action)
    {
        SetFlag("true");
        var (student, user) = await Factory.LoginAsAsync("student");
        using var client = student;
        using var admin = await Factory.LoginAsync("admin");
        var teacher = await Factory.CreateUserAsync("teacher");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, user.Id);
        using var reported = await ReportAsync(client, lesson.Id, Concern);
        reported.EnsureSuccessStatusCode();
        var incident = (await reported.Content.ReadFromJsonAsync<LessonIncidentDto>())!;

        using var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incident.Id}/resolve",
            new { action, note = ReviewNote });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("open", await Factory.WithDbAsync(db => db.LessonIncidents.Where(x => x.Id == incident.Id)
            .Select(x => x.Status).SingleAsync()));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.TeacherPenalties.CountAsync(x => x.LessonId == lesson.Id)));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.ReliabilityEvents.CountAsync(x => x.LessonId == lesson.Id)));
    }

    private void SetFlag(string? value) =>
        Factory.Services.GetRequiredService<IConfiguration>()["LessonRoom:ReportConcern"] = value;

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, Guid lessonId, string? detail) =>
        client.PostAsJsonAsync($"/api/lessons/{lessonId}/incident", new { type = "conduct_concern", detail });

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AddRoleAsync(AcademyUser user, string role)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AcademyUser>>();
        var tracked = await users.FindByIdAsync(user.Id.ToString());
        var result = await users.AddToRoleAsync(tracked!, role);
        Assert.True(result.Succeeded);
    }

    private sealed class PushCapture : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Messages { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            foreach (var message in payload.RootElement.EnumerateArray()) Messages.Enqueue(message.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = Array.Empty<object>() }),
            };
        }
    }
}
