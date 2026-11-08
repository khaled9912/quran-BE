using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The reset request must answer the same way whether or not the address is
/// registered, otherwise the endpoint is a free account list.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PasswordResetEnumerationTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task KnownAndUnknownAddressGetTheSameAnswer()
    {
        var member = await Factory.CreateUserAsync("student");
        var unknown = $"ghost.{Guid.NewGuid():N}@example.test";

        using var known = await Factory.Client.PostAsJsonAsync("/api/auth/password/request", new { email = member.Email });
        using var ghost = await Factory.Client.PostAsJsonAsync("/api/auth/password/request", new { email = unknown });

        Assert.Equal(known.StatusCode, ghost.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await ghost.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResponseDoesNotCarryADeveloperCodeOutsideDevelopment()
    {
        var member = await Factory.CreateUserAsync("student");

        var response = await Factory.Client.PostAsJsonAsync("/api/auth/password/request", new { email = member.Email });

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"devCode\":\"", text, StringComparison.Ordinal);
        Assert.Contains("\"devCode\":null", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidEmailIsRejectedButNeverRevealsAnAccount()
    {
        var response = await Factory.Client.PostAsJsonAsync("/api/auth/password/request", new { email = "not-an-email" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>Reset codes: single use, and bound to the address they were issued for.</summary>
[Collection(SecurityCollection.Name)]
public sealed class PasswordResetCodeTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task UnknownAddressCodeIsRejectedAsUnauthorized()
    {
        var response = await Factory.Client.PostAsJsonAsync("/api/auth/password/reset", new
        {
            email = "member-unknown@example.test",
            code = "123456",
            newPassword = "Rotated!Passw0rd",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IssuedCodeResetsThePasswordAndIsThenSpent()
    {
        var member = await Factory.CreateUserAsync("student");
        var code = Factory.Resets.TryIssue(member.Email!.Trim().ToLowerInvariant())!;

        var reset = await Factory.Client.PostAsJsonAsync("/api/auth/password/reset", new
        {
            email = member.Email,
            code,
            newPassword = "Rotated!Passw0rd",
        });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.True(await Factory.WithDbAsync(db => db.AuditLogs.AnyAsync(x =>
            x.Action == "password_reset_completed" && x.EntityId == member.Id)));

        var replay = await Factory.Client.PostAsJsonAsync("/api/auth/password/reset", new
        {
            email = member.Email,
            code,
            newPassword = "Another!Passw0rd",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task WeakNewPasswordIsRejectedBeforeTheCodeIsSpent()
    {
        var member = await Factory.CreateUserAsync("student");
        var key = member.Email!.Trim().ToLowerInvariant();
        var code = Factory.Resets.TryIssue(key)!;

        var weak = await Factory.Client.PostAsJsonAsync("/api/auth/password/reset", new
        {
            email = member.Email,
            code,
            newPassword = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        // The code is still live, so the user can retry with a real password.
        Assert.True(Factory.Resets.Check(key, code!));
    }
}
