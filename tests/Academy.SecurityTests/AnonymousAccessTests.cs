using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Xunit;

namespace Academy.SecurityTests;

[Collection(SecurityCollection.Name)]
public sealed class AnonymousAccessTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    public static readonly TheoryData<string, string> PrivateEndpoints = new()
    {
        { "GET", "/api/auth/me" },
        { "GET", "/api/profile" },
        { "GET", "/api/lessons" },
        { "GET", "/api/profile/reports" },
        { "GET", "/api/students" },
        { "GET", "/api/teacher/hours" },
        { "GET", "/api/student/series" },
        { "GET", "/api/teacher/billing" },
        { "GET", "/api/student/billing" },
        { "GET", "/api/admin/billing" },
        { "GET", "/api/admin/lesson-incidents" },
        { "GET", "/api/admin/teacher-applications" },
        { "GET", "/api/admin/transcripts" },
        { "GET", "/api/admin/review-queue" },
        { "GET", "/api/notifications" },
    };

    [Theory]
    [MemberData(nameof(PrivateEndpoints))]
    public async Task AnonymousRequestIsRejected(string method, string path)
    {
        var client = Factory.Client;
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostToAdminUserCreationIsRejectedWithoutSession()
    {
        var response = await Factory.Client.PostAsJsonAsync("/api/admin/users",
            new { fullName = "Nobody", email = "nobody@example.test", password = "Str0ng!Passw0rd", role = "admin" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnmatchedApiRouteFailsClosedRatherThanOpening()
    {
        // The fallback policy is the guard for a future endpoint that forgets to
        // declare its own authorization, so an unknown path must not be public.
        var response = await Factory.Client.GetAsync("/api/something-not-mapped");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerIsNotServedOutsideDevelopment()
    {
        var swagger = await Factory.Client.GetAsync("/swagger/index.html");
        var json = await Factory.Client.GetAsync("/swagger/v1/swagger.json");

        Assert.False(swagger.IsSuccessStatusCode);
        Assert.False(json.IsSuccessStatusCode);
    }

    [Fact]
    public async Task HealthEndpointStaysAnonymous()
    {
        var response = await Factory.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SessionProbeDoesNotLeakWhenCookieIsGarbage()
    {
        var client = Factory.Client;
        client.DefaultRequestHeaders.Add("Cookie", "academy-auth=not-a-real-ticket");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
