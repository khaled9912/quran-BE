using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Rebinding a sign-in identifier is the step between a stolen session and a
/// permanent account takeover, so it must cost an existing credential.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class ProfileStepUpTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task EmailChangeWithoutProofIsRejectedAndNotApplied()
    {
        var (client, user) = await Factory.LoginAsAsync("student");
        var newEmail = $"taken.{Guid.NewGuid():N}@example.test";

        var response = await client.PatchAsJsonAsync("/api/profile", new { email = newEmail });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(user.Email, await Factory.WithDbAsync(db => Task.FromResult(
            db.Users.AsNoTracking().Single(x => x.Id == user.Id).Email)));
    }

    [Fact]
    public async Task PhoneChangeWithoutProofIsRejected()
    {
        var (client, _) = await Factory.LoginAsAsync("student");

        var response = await client.PatchAsJsonAsync("/api/profile", new { phoneNumber = "+96170000" + Random.Shared.Next(1000, 9999) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordIsNotStepUpProof()
    {
        var (client, _) = await Factory.LoginAsAsync("student");

        var response = await client.PatchAsJsonAsync("/api/profile", new
        {
            email = $"guessed.{Guid.NewGuid():N}@example.test",
            currentPassword = "not-the-users-password",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmailChangeWithCurrentPasswordIsAppliedAndAudited()
    {
        var (client, user) = await Factory.LoginAsAsync("student");
        var newEmail = $"moved.{Guid.NewGuid():N}@example.test";

        var response = await client.PatchAsJsonAsync("/api/profile", new
        {
            email = newEmail,
            currentPassword = ApiFactory.TestPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newEmail, await Factory.WithDbAsync(db => Task.FromResult(
            db.Users.AsNoTracking().Single(x => x.Id == user.Id).Email)));
        Assert.True(await Factory.WithDbAsync(db => db.AuditLogs.AnyAsync(x =>
            x.Action == "profile_identifier_changed" && x.EntityId == user.Id)));
    }

    [Fact]
    public async Task PhoneChangeWithOtpCodeIsApplied()
    {
        var phone = "+961701" + Random.Shared.Next(100000, 999999);
        var (client, _) = await Factory.LoginAsAsync("student", phone, phoneConfirmed: true);
        var code = Factory.Otp.TryIssue(phone);
        Assert.NotNull(code);

        var response = await client.PatchAsJsonAsync("/api/profile", new
        {
            phoneNumber = "+961702" + Random.Shared.Next(100000, 999999),
            phoneCode = code,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The code is single-use: the same proof cannot authorize a second change.
        var replay = await client.PatchAsJsonAsync("/api/profile", new
        {
            phoneNumber = "+961703" + Random.Shared.Next(100000, 999999),
            phoneCode = code,
        });
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    public async Task OrdinaryProfileEditsStillNeedNoProof()
    {
        var (client, _) = await Factory.LoginAsAsync("student");

        var response = await client.PatchAsJsonAsync("/api/profile", new { fullName = "A Different Name" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangingToAnEmailThatAlreadyExistsIsRejected()
    {
        var (client, _) = await Factory.LoginAsAsync("student");
        var existing = await Factory.CreateUserAsync("teacher");

        var response = await client.PatchAsJsonAsync("/api/profile", new
        {
            email = existing.Email,
            currentPassword = ApiFactory.TestPassword,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
