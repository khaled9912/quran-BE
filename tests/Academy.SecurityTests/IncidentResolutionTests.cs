using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// An incident on a lesson that has already settled its credit, strike and
/// payable outcome must not settle them a second time.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class IncidentResolutionTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task ResolvingAnIncidentOnACancelledLessonDoesNotRepeatTheSettlement()
    {
        var admin = await Factory.LoginAsync("admin");
        var lessonId = await CancelledLessonAsync();
        var incidentId = await OpenIncidentAsync(lessonId, "potential_teacher_no_show");

        var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incidentId}/resolve", new
        {
            action = "teacher_no_show",
            note = "Verified against the room log.",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonCancellations.CountAsync(x => x.LessonId == lessonId)));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.LessonCreditTransactions.CountAsync(
            x => x.DedupeKey == $"teacher-no-show:{lessonId}" || x.DedupeKey == $"no-show:{lessonId}")));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.ReliabilityEvents.CountAsync(x => x.LessonId == lessonId)));
        Assert.Equal("resolved", await Factory.WithDbAsync(db => Task.FromResult(
            db.LessonIncidents.AsNoTracking().Single(x => x.Id == incidentId).Status)));
    }

    [Fact]
    public async Task ResolvingANewIncidentSettlesTheLessonExactlyOnce()
    {
        var admin = await Factory.LoginAsync("admin");
        var (lessonId, studentId) = await LessonAsync();
        var incidentId = await OpenIncidentAsync(lessonId, "potential_student_no_show");

        var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incidentId}/resolve", new
        {
            action = "student_no_show",
            note = "Teacher confirmed the wait.",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonCancellations.CountAsync(x => x.LessonId == lessonId)));
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonCreditTransactions.CountAsync(
            x => x.DedupeKey == $"no-show:{lessonId}" && x.StudentId == studentId && x.Amount == -1)));
        Assert.Equal("cancelled", await Factory.WithDbAsync(db => Task.FromResult(
            db.Lessons.AsNoTracking().Single(x => x.Id == lessonId).Status)));
    }

    [Fact]
    public async Task AlreadyResolvedIncidentCannotBeResolvedAgain()
    {
        var admin = await Factory.LoginAsync("admin");
        var (lessonId, _) = await LessonAsync();
        var incidentId = await Factory.WithDbAsync(async db =>
        {
            var incident = new LessonIncident
            {
                LessonId = lessonId,
                Type = "attendance_dispute",
                Detail = "Both sides disagree.",
                Status = "resolved",
            };
            db.LessonIncidents.Add(incident);
            await db.SaveChangesAsync();
            return incident.Id;
        });

        var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incidentId}/resolve", new
        {
            action = "dismissed",
            note = "Second attempt.",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TeacherCannotResolveIncidents()
    {
        var teacher = await Factory.LoginAsync("teacher");
        var (lessonId, _) = await LessonAsync();
        var incidentId = await OpenIncidentAsync(lessonId, "attendance_dispute");

        var response = await teacher.PostAsJsonAsync($"/api/admin/lesson-incidents/{incidentId}/resolve", new
        {
            action = "dismissed",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("open", await Factory.WithDbAsync(db => Task.FromResult(
            db.LessonIncidents.AsNoTracking().Single(x => x.Id == incidentId).Status)));
    }

    [Fact]
    public async Task UnknownActionIsRefused()
    {
        var admin = await Factory.LoginAsync("admin");
        var (lessonId, _) = await LessonAsync();
        var incidentId = await OpenIncidentAsync(lessonId, "attendance_dispute");

        var response = await admin.PostAsJsonAsync($"/api/admin/lesson-incidents/{incidentId}/resolve", new
        {
            action = "pay_anyway",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> CancelledLessonAsync()
    {
        var (lessonId, _) = await LessonAsync();
        var starts = DateTimeOffset.UtcNow.AddDays(-2);
        await Factory.WithDbAsync(async db =>
        {
            var lesson = await db.Lessons.SingleAsync(x => x.Id == lessonId);
            lesson.Status = "cancelled";
            lesson.CancelReason = "Cancelled while the incident was open";
            lesson.CancelledAt = starts;
            db.LessonCancellations.Add(new LessonCancellation
            {
                LessonId = lessonId,
                ActorRole = "admin",
                Outcome = "admin_cancelled",
                IsLate = true,
                TeacherPayableMinutes = 0,
                Reason = "Cancelled while the incident was open",
                CreatedAt = starts,
            });
            await db.SaveChangesAsync();
        });
        return lessonId;
    }

    private async Task<(Guid LessonId, Guid StudentId)> LessonAsync()
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var student = await Factory.CreateUserAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacher.Id, student.Id);
        var starts = DateTimeOffset.UtcNow.AddDays(-2);
        await Factory.WithDbAsync(async db =>
        {
            var row = await db.Lessons.SingleAsync(x => x.Id == lesson.Id);
            row.StartsAt = starts;
            row.EndsAt = starts.AddMinutes(30);
            await db.SaveChangesAsync();
        });
        return (lesson.Id, student.Id);
    }

    private async Task<Guid> OpenIncidentAsync(Guid lessonId, string type) =>
        await Factory.WithDbAsync(async db =>
        {
            var incident = new LessonIncident { LessonId = lessonId, Type = type, Detail = "Reported by the room monitor." };
            db.LessonIncidents.Add(incident);
            await db.SaveChangesAsync();
            return incident.Id;
        });
}
