using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Session revocation. The session cookie lives for seven days, so the only way
/// an account can pull back access it has already granted is the security stamp.
/// GET /api/lessons is the probe: it answers from claims alone and never looks
/// up IsActive, so a 200 there proves the cookie is still being accepted.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class SessionRevocationTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task DeactivatedAccountLosesItsExistingSession()
    {
        var admin = await Factory.LoginAsync("admin");
        var (student, studentUser) = await Factory.LoginAsAsync("student");

        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/lessons")).StatusCode);

        var deactivate = await admin.DeleteAsync($"/api/admin/users/{studentUser.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await student.GetAsync("/api/lessons")).StatusCode);
    }

    [Fact]
    public async Task AdminSetPasswordInvalidatesTheOldSession()
    {
        // The usual response to a compromised account: an admin sets a new password
        // and expects the attacker's device to be out.
        var admin = await Factory.LoginAsync("admin");
        var (teacher, teacherUser) = await Factory.LoginAsAsync("teacher");

        Assert.Equal(HttpStatusCode.OK, (await teacher.GetAsync("/api/lessons")).StatusCode);

        var patch = await admin.PatchAsJsonAsync($"/api/admin/users/{teacherUser.Id}",
            new { password = "Str0ng!Passw0rdRotated" });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await teacher.GetAsync("/api/lessons")).StatusCode);
    }

    [Fact]
    public async Task SelfServicePasswordChangeKeepsThisDeviceSignedIn()
    {
        var (student, studentUser) = await Factory.LoginAsAsync("student");
        // A second browser on the same account, representing an attacker's session.
        var otherDevice = await Factory.SignInAsync(studentUser.Email);

        var change = await student.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = ApiFactory.TestPassword,
            newPassword = "Str0ng!Passw0rdRotated",
        });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/lessons")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/api/lessons")).StatusCode);
    }
}
