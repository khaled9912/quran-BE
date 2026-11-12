using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The admin queue holds a stranger's CV, phone number and voice recordings.
/// Authorization itself is already pinned by RoleMatrixTests and
/// AnonymousAccessTests; what is pinned here is that the answers are not
/// cacheable and that reading an applicant's documents leaves a trace.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TeacherApplicationAdminTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task AdminListingIsNeverCacheable()
    {
        await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");

        var response = await admin.GetAsync("/api/admin/teacher-applications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OpeningAnApplicantsCvIsWrittenToTheAuditTrail()
    {
        var id = await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");

        var response = await admin.GetAsync($"/api/admin/teacher-applications/{id}/cv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await Factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityId == id)
            .Select(x => new { x.Action, x.EntityType, x.Detail, x.SourceIp })
            .ToListAsync());

        Assert.Equal(new[] { new { Action = "teacher_application_file_viewed", EntityType = "TeacherApplication", Detail = "cv", SourceIp = (string?)ApiFactory.TestRemoteAddress } },
            audit);
    }

    [Fact]
    public async Task AnAttachmentThatWasNeverUploadedStaysUnaudited()
    {
        // The applicant sent no self-introduction, so there is nothing to read and
        // nothing to claim later that was.
        var id = await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");

        var response = await admin.GetAsync($"/api/admin/teacher-applications/{id}/intro");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await Factory.WithDbAsync(db => db.AuditLogs.AnyAsync(x => x.EntityId == id)));
    }

    [Fact]
    public async Task RejectingAnApplicationIsAttributedToTheReviewer()
    {
        var id = await SubmitAsync();
        var (admin, reviewer) = await Factory.LoginAsAsync("admin");

        var response = await admin.PatchAsJsonAsync($"/api/admin/teacher-applications/{id}",
            new { status = "rejected", reason = "No teaching reference supplied." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await Factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityId == id)
            .Select(x => new { x.ActorId, x.Action, x.Detail })
            .ToListAsync());

        Assert.Equal(new[] { new { ActorId = (Guid?)reviewer.Id, Action = "teacher_application_rejected", Detail = "No teaching reference supplied." } },
            audit);
    }

    /// <summary>
    /// Submits exactly what the public /teach form sends, minus the optional
    /// introduction recording, and hands back the application id.
    /// </summary>
    private async Task<Guid> SubmitAsync()
    {
        var cv = new ByteArrayContent("%PDF-1.4 curriculum vitae"u8.ToArray());
        cv.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var recitation = new ByteArrayContent(OggSkeleton());
        recitation.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

        var form = new MultipartFormDataContent
        {
            { new StringContent("Amina Teacher"), "fullName" },
            { new StringContent($"applicant.{Guid.NewGuid():N}@example.test"), "email" },
            { new StringContent("+15550001234"), "phoneNumber" },
            { new StringContent("quran"), "subjects" },
            { new StringContent("fluent"), "englishLevel" },
            { new StringContent("5"), "experienceYears" },
            { new StringContent("I have taught Quran to beginners for five years."), "bio" },
            { cv, "cv", "cv.pdf" },
            { recitation, "quran", "recitation.ogg" },
        };

        var response = await Factory.Client.PostAsync("/api/teacher-applications", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SubmittedApplication>();

        return body!.Id;
    }

    /// <summary>The validator wants the OggS magic and at least twelve bytes.</summary>
    private static byte[] OggSkeleton()
    {
        var bytes = new byte[32];
        "OggS"u8.CopyTo(bytes);
        return bytes;
    }

    private sealed record SubmittedApplication(Guid Id, string Status);
}
