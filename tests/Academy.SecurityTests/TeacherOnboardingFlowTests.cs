using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The owner's rule: approving an application must not create the teacher
/// account. The account is born only when an admin finishes onboarding after
/// the intro meeting, and until then the applicant picks and joins without one.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TeacherOnboardingFlowTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task ApprovalCreatesNoAccountAndFinishIsRefusedBeforeTheMeeting()
    {
        var (id, email) = await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");

        using var review = await admin.PatchAsJsonAsync($"/api/admin/teacher-applications/{id}",
            new { status = "approved" });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        Assert.False(await AccountExistsAsync(email));
        Assert.Null(await TeacherIdAsync(id));

        using var finish = await admin.PostAsync($"/api/admin/teacher-applications/{id}/finish-onboarding", null);
        Assert.Equal(HttpStatusCode.Conflict, finish.StatusCode);
        Assert.False(await AccountExistsAsync(email));
    }

    [Fact]
    public async Task TheApplicantPicksWithoutAnAccountAndFinishCreatesIt()
    {
        var (id, email) = await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(
            $"/api/admin/teacher-applications/{id}", new { status = "approved" })).StatusCode);

        var slot = DateTimeOffset.UtcNow.AddHours(2);
        using var propose = await admin.PostAsJsonAsync(
            $"/api/admin/teacher-applications/{id}/meeting-slots",
            new { slots = new[] { slot.ToString("o") } });
        Assert.Equal(HttpStatusCode.OK, propose.StatusCode);
        var proposed = await propose.Content.ReadFromJsonAsync<ProposeResult>();
        // Email is disabled in tests, so the desk gets the link to deliver by hand.
        Assert.False(proposed!.Emailed);
        var token = proposed.PickerUrl!.Split('/').Last();

        using var pick = await Factory.Client.PostAsJsonAsync(
            $"/api/onboarding/{token}/pick", new { slot = slot.ToString("o") });
        Assert.Equal(HttpStatusCode.OK, pick.StatusCode);
        var booked = await pick.Content.ReadFromJsonAsync<PickResult>();
        Assert.Contains("anaquran-onboarding-", booked!.JoinUrl);
        Assert.False(await AccountExistsAsync(email));
        Assert.Null(await TeacherIdAsync(id));

        // The single-use link dies with the pick.
        using var reuse = await Factory.Client.GetAsync($"/api/onboarding/{token}");
        Assert.Equal(HttpStatusCode.NotFound, reuse.StatusCode);

        using var finish = await admin.PostAsync($"/api/admin/teacher-applications/{id}/finish-onboarding", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var finished = await finish.Content.ReadFromJsonAsync<FinishResult>();
        Assert.NotNull(finished!.InviteCode);
        Assert.NotNull(finished.Application.TeacherId);
        Assert.NotNull(finished.Application.OnboardedAt);
        Assert.True(await AccountExistsAsync(email));

        using var again = await admin.PostAsync($"/api/admin/teacher-applications/{id}/finish-onboarding", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AnApprovedApplicationCanBeRejectedUntilTheAccountExists()
    {
        var (id, email) = await SubmitAsync();
        var admin = await Factory.LoginAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(
            $"/api/admin/teacher-applications/{id}", new { status = "approved" })).StatusCode);

        using var reject = await admin.PatchAsJsonAsync($"/api/admin/teacher-applications/{id}",
            new { status = "rejected", reason = "The intro meeting did not go well." });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.False(await AccountExistsAsync(email));
    }

    private Task<bool> AccountExistsAsync(string email) =>
        Factory.WithDbAsync(db => db.Users.AnyAsync(x => x.Email == email));

    private Task<Guid?> TeacherIdAsync(Guid id) =>
        Factory.WithDbAsync(db => db.TeacherApplications
            .Where(x => x.Id == id).Select(x => x.TeacherId).SingleAsync());

    private async Task<(Guid Id, string Email)> SubmitAsync()
    {
        var email = $"applicant.{Guid.NewGuid():N}@example.test";
        var cv = new ByteArrayContent("%PDF-1.4 curriculum vitae"u8.ToArray());
        cv.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var recitation = new ByteArrayContent(OggSkeleton());
        recitation.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

        var form = new MultipartFormDataContent
        {
            { new StringContent("Amina Teacher"), "fullName" },
            { new StringContent(email), "email" },
            { new StringContent("+15550001234"), "phoneNumber" },
            { new StringContent("quran"), "subjects" },
            { new StringContent("fluent"), "englishLevel" },
            { new StringContent("5"), "experienceYears" },
            { new StringContent("I have taught Quran to beginners for five years."), "bio" },
            { cv, "cv", "cv.pdf" },
            { recitation, "quran", "recitation.ogg" },
        };

        using var response = await Factory.Client.PostAsync("/api/teacher-applications", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SubmittedApplication>();
        return (body!.Id, email);
    }

    private static byte[] OggSkeleton()
    {
        var bytes = new byte[32];
        "OggS"u8.CopyTo(bytes);
        return bytes;
    }

    private sealed record SubmittedApplication(Guid Id, string Status);
    private sealed record ProposeResult(bool Emailed, string? PickerUrl);
    private sealed record PickResult(string JoinUrl);
    private sealed record FinishResult(ApplicationRow Application, string? InviteCode);
    private sealed record ApplicationRow(Guid? TeacherId, DateTimeOffset? OnboardedAt);
}