using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Privilege escalation across the four roles. Every route here is reachable, so
/// a 200 would mean the policy is missing rather than the route being absent.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class RoleMatrixTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task TeacherCannotCreateAdminAccounts()
    {
        var teacher = await Factory.LoginAsync("teacher");

        var response = await teacher.PostAsJsonAsync("/api/admin/users", new
        {
            fullName = "Escalated Teacher",
            email = "escalated@example.test",
            password = ApiFactory.TestPassword,
            role = "admin",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await Factory.WithDbAsync(db => Task.FromResult(
            db.Users.Any(u => u.Email == "escalated@example.test"))));
    }

    [Fact]
    public async Task StudentCannotReadTheAdminDirectory()
    {
        var student = await Factory.LoginAsync("student");

        var response = await student.GetAsync("/api/admin/teacher-applications");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SupportCannotReadTheAdminDirectory()
    {
        // Support reviews transcripts; it is deliberately not an admin.
        var support = await Factory.LoginAsync("support");

        var response = await support.GetAsync("/api/admin/teacher-applications");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SupportMayReadTheReviewQueue()
    {
        var support = await Factory.LoginAsync("support");

        var response = await support.GetAsync("/api/admin/review-queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TeacherCannotUseStudentEndpoints()
    {
        var teacher = await Factory.LoginAsync("teacher");

        var response = await teacher.GetAsync("/api/student/series");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StudentCannotUseTeacherEndpoints()
    {
        var student = await Factory.LoginAsync("student");

        var response = await student.GetAsync("/api/teacher/hours");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StudentCannotPublishTeacherAvailability()
    {
        var student = await Factory.LoginAsync("student");

        var response = await student.PostAsJsonAsync("/api/teacher/availability", new
        {
            startsAt = DateTimeOffset.UtcNow.AddDays(2),
            endsAt = DateTimeOffset.UtcNow.AddDays(2).AddMinutes(30),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TeacherCannotChangeTheirOwnPayoutRate()
    {
        // Rates are an academy decision; a teacher must not be able to set their own.
        var teacher = await Factory.LoginAsync("teacher");

        var response = await teacher.PutAsJsonAsync("/api/admin/billing/rates/00000000-0000-0000-0000-000000000000",
            new { studentPriceUsd = 999m, payoutEgp = 999999m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanCreateTeacherAndAuditRecordsIt()
    {
        var admin = await Factory.LoginAsync("admin");
        var before = await Factory.WithDbAsync(db => db.AuditLogs.CountAsync());

        var response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            fullName = "New Teacher",
            email = $"newteacher.{Guid.NewGuid():N}@example.test",
            password = ApiFactory.TestPassword,
            role = "teacher",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Factory.WithDbAsync(db => Task.FromResult(
            db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "admin_user_created")
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefault()));
        Assert.True(await Factory.WithDbAsync(db => db.AuditLogs.CountAsync()) > before);
        Assert.NotNull(created);
        Assert.Equal("admin_user_created", created.Action);
        Assert.Equal("User", created.EntityType);
        Assert.DoesNotContain(ApiFactory.TestPassword, created.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditRowsCarryActorRoleSourceIpAndCorrelationId()
    {
        // An audit entry is only useful during an incident if it can be lined up
        // with the hosting provider's access log, which needs role + IP + request id.
        var (admin, actor) = await Factory.LoginAsAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            fullName = "Audited Teacher",
            email = $"auditedteacher.{Guid.NewGuid():N}@example.test",
            password = ApiFactory.TestPassword,
            role = "teacher",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Factory.WithDbAsync(db => Task.FromResult(
            db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "admin_user_created" && a.ActorId == actor.Id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefault()));

        Assert.NotNull(created);
        Assert.Equal("admin", created.ActorRole);
        Assert.Equal(ApiFactory.TestRemoteAddress, created.SourceIp);
        Assert.False(string.IsNullOrWhiteSpace(created.CorrelationId));
    }
}
