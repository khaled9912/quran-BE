using System.Net;
using System.Net.Http.Json;
using Academy.SecurityTests.Infrastructure;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Headers the API owns. Host-level ones (Server, X-Powered-By) are stripped in
/// web.config, so they are deliberately not asserted here.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class ResponseHardeningTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task AnonymousResponsesCarryTheBrowserGuards()
    {
        var response = await Factory.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task LoginCookieIsHttpOnlySecureAndNamed()
    {
        var user = await Factory.CreateUserAsync("student");

        // HandleCookies is off: a cookie jar would swallow Set-Cookie before the
        // attributes could be checked.
        var response = await Factory.RawClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = user.Email,
            password = ApiFactory.TestPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = string.Join(" ", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("academy-auth=", setCookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignOriginMutationIsRefused()
    {
        // Cookie authentication plus a cross-origin write is the CSRF shape this
        // check exists for. A browser always sends Origin on a POST.
        var client = await Factory.LoginAsync("student");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contact")
        {
            Content = JsonContent.Create(new { name = "A Student", email = "a@example.test", message = "Hello there." }),
        };
        request.Headers.Add("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllowedOriginMutationPassesTheOriginGate()
    {
        var client = await Factory.LoginAsync("student");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contact")
        {
            Content = JsonContent.Create(new { name = "A Student", email = "a@example.test", message = "Hello there." }),
        };
        request.Headers.Add("Origin", ApiFactory.AllowedOrigin);

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

/// <summary>The anonymous contact form is the cheapest endpoint to spam.</summary>
[Collection(SecurityCollection.Name)]
public sealed class ContactRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task SixthRequestInTheWindowIsRefusedAndSaysWhenToReturn()
    {
        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 6; attempt++)
            last = await Factory.Client.PostAsJsonAsync("/api/contact", new
            {
                name = "A Visitor",
                email = "visitor@example.test",
                message = "Repeating the same enquiry over and over.",
            });

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.NotNull(last.Headers.RetryAfter);
    }
}

/// <summary>Five misses lock the account, and the lock must survive the right password.</summary>
[Collection(SecurityCollection.Name)]
public sealed class SignInLockoutTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task LockedAccountCannotBeSignedIntoWithTheRealPassword()
    {
        var user = await Factory.CreateUserAsync("student");
        var attacker = Factory.Client;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var miss = await attacker.PostAsJsonAsync("/api/auth/login", new
            {
                email = user.Email,
                password = "Wrong-Passw0rd!",
            });
            // Identity locks on the attempt that reaches the cap, so the fifth
            // miss already reports the lock rather than bad credentials.
            Assert.True(miss.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"Attempt {attempt + 1} returned {(int)miss.StatusCode}.");
        }

        var response = await attacker.PostAsJsonAsync("/api/auth/login", new
        {
            email = user.Email,
            password = ApiFactory.TestPassword,
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("Sign in locked", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownEmailAndWrongPasswordLookIdentical()
    {
        var user = await Factory.CreateUserAsync("student");
        var ghost = await Factory.Client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"ghost.{Guid.NewGuid():N}@example.test",
            password = ApiFactory.TestPassword,
        });
        var miss = await Factory.Client.PostAsJsonAsync("/api/auth/login", new
        {
            email = user.Email,
            password = "Wrong-Passw0rd!",
        });

        Assert.Equal(ghost.StatusCode, miss.StatusCode);
        Assert.Equal(await ghost.Content.ReadAsStringAsync(), await miss.Content.ReadAsStringAsync());
    }
}
