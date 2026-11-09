using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Web Push subscriptions are an URL the API is later asked to POST to with its
/// own signing key attached, so input validation is the only thing between that
/// and an attacker using the server as a relay.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class WebPushSubscriptionTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string FcmEndpoint =
        "https://fcm.googleapis.com/wpush/v2/AF1QipP5hkT0dKTESTSUBSCRIPTIONaBcDeFgHiJkLmNoPqRsTuVwXyZ";
    private const string P256Dh =
        "BN32sT5T4lHrj0V4G9lQ2x5Q8c6rG1sZ7tY0pO3iU6eA8dF2gH4jK6lM0nB4cV6xZ8aQ0wE2rT4yU6iO8p=";
    private const string AuthKey = "dGVzdC1hdXRoLXNlY3JldA";

    private static string Body(string endpoint, string p256dh = P256Dh, string auth = AuthKey) =>
        $$"""{"endpoint":"{{endpoint}}","p256dh":"{{p256dh}}","auth":"{{auth}}"}""";

    [Fact]
    public async Task SubscribeRequiresASession()
    {
        var response = await Factory.Client.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KeyEndpointNeverReachesAnAnonymousCaller()
    {
        var response = await Factory.Client.GetAsync("/api/web-push/key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    // Anything the server would POST to itself, or to a host reachable only from
    // the hosting box, is refused before it is ever stored.
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://localhost:5001/api/admin/users")]
    [InlineData("https://anaquran.runasp.net/api/admin/users")]
    [InlineData("https://attacker.example/wpush/v2/x")]
    [InlineData("fcm.googleapis.com/wpush/v2/x")]
    [InlineData("https://fcm.googleapis.com@attacker.example/wpush/v2/x")]
    [InlineData("https://evilmirror.com/wpush")]
    public async Task AnEndpointFromAnUnknownPushServiceIsRefused(string endpoint)
    {
        var client = await Factory.LoginAsync("student");
        var response = await client.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(endpoint), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.BrowserSubscriptions.CountAsync()));
    }

    [Fact]
    public async Task ARealPushServiceEndpointIsStoredAgainstTheSignedInAccount()
    {
        var (client, user) = await Factory.LoginAsAsync("student");
        var response = await client.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await Factory.WithDbAsync(db => db.BrowserSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Endpoint == FcmEndpoint));
        Assert.NotNull(row);
        Assert.Equal(user.Id, row!.UserId);
    }

    [Fact]
    public async Task IncompleteSubscriptionKeysAreRefused()
    {
        var client = await Factory.LoginAsync("student");
        var response = await client.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint, p256dh: "short"), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.BrowserSubscriptions.CountAsync()));
    }

    [Fact]
    public async Task SigningInElsewhereMovesTheBrowserInsteadOfDuplicatingIt()
    {
        // One browser profile has one subscription. The row follows whoever is
        // signed in now, or the previous account would keep being rung on a
        // laptop that belongs to someone else.
        var first = await Factory.LoginAsync("student");
        await first.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        var (second, secondUser) = await Factory.LoginAsAsync("teacher");
        await second.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        var rows = await Factory.WithDbAsync(db => db.BrowserSubscriptions.AsNoTracking().ToListAsync());
        Assert.Single(rows);
        Assert.Equal(secondUser.Id, rows[0].UserId);
    }

    [Fact]
    public async Task AnotherAccountCannotDropSomeoneElsesSubscription()
    {
        var owner = await Factory.LoginAsync("student");
        await owner.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        var stranger = await Factory.LoginAsync("teacher");
        var response = await stranger.PostAsync(
            "/api/web-push/unsubscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.BrowserSubscriptions.CountAsync()));
    }

    [Fact]
    public async Task TheOwnerCanDropTheirOwnSubscription()
    {
        var client = await Factory.LoginAsync("student");
        await client.PostAsync(
            "/api/web-push/subscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        var response = await client.PostAsync(
            "/api/web-push/unsubscribe", new StringContent(Body(FcmEndpoint), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.BrowserSubscriptions.CountAsync()));
    }

    /// <summary>
    /// Neither transport is configured in these tests, so the delivery fan-out
    /// has to find nothing and still let the call through: a missing push
    /// credential must never be the reason a teacher cannot ring a student.
    /// </summary>
    [Fact]
    public async Task ACallIsRecordedWhenNoPushTransportIsConfigured()
    {
        var (teacher, teacherUser) = await Factory.LoginAsAsync("teacher");
        var (_, studentUser) = await Factory.LoginAsAsync("student");
        var lesson = await Factory.CreateLessonAsync(teacherUser.Id, studentUser.Id);

        // The helper books a day ahead; calls only open near the lesson.
        await Factory.WithDbAsync(async db =>
        {
            var row = await db.Lessons.FirstAsync(x => x.Id == lesson.Id);
            row.StartsAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            row.EndsAt = DateTimeOffset.UtcNow.AddMinutes(28);
            await db.SaveChangesAsync();
        });

        var response = await teacher.PostAsJsonAsync($"/api/lessons/{lesson.Id}/call", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.LessonCalls.CountAsync(x => x.LessonId == lesson.Id)));
        Assert.Equal(1, await Factory.WithDbAsync(db =>
            db.Notifications.CountAsync(x => x.UserId == studentUser.Id && x.Link == $"/lesson/{lesson.Id}")));
    }
}
