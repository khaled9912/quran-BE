using Academy.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Configuration parsing that decides what the running app trusts. Neither test
/// group below needs a database, so they are the fastest proof that a settings
/// change did what the operator intended.
/// </summary>
public sealed class ForwardingTests
{
    private static (bool Enabled, ForwardedHeadersOptions Options, List<string> Notices) Configure(string? proxies, string? hosts)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forwarding:TrustedProxies"] = proxies,
            ["Forwarding:AllowedHosts"] = hosts,
        }).Build();

        var notices = new List<string>();
        var enabled = Forwarding.TryConfigure(services, config, notices.Add);
        var options = enabled
            ? services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value
            : new ForwardedHeadersOptions();
        return (enabled, options, notices);
    }

    [Fact]
    public void NoProxyListMeansNoForwardedHeadersAreAccepted()
    {
        var (enabled, _, notices) = Configure(null, "anaquran.com");

        Assert.False(enabled);
        Assert.Empty(notices);
    }

    [Fact]
    public void ConfiguredProxiesBecomeTheOnlyTrustedSourceOfTheClientAddress()
    {
        var (enabled, options, _) = Configure("76.76.21.1, 76.76.21.2", null);

        Assert.True(enabled);
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.DoesNotContain(options.KnownProxies, address => address.ToString() == "127.0.0.1");
        Assert.Equal(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
    }

    [Fact]
    public void TheHostHeaderIsOnlyTrustedOnceTheValidHostsAreNamed()
    {
        var withoutHosts = Configure("76.76.21.1", null);
        var withHosts = Configure("76.76.21.1", "anaquran.com, *.anaquran.com");

        Assert.False(withoutHosts.Options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.True(withHosts.Options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.Equal(["anaquran.com", "*.anaquran.com"], withHosts.Options.AllowedHosts);
    }

    [Fact]
    public void SubnetsAndTyposAreRefusedInsteadOfSilentlyWideningTrust()
    {
        var subnet = Configure("203.0.113.0/24", null);
        var typo = Configure("not-an-address", null);
        var oneBadOneGood = Configure("203.0.113.0/24, 76.76.21.1", null);

        Assert.False(subnet.Enabled);
        Assert.False(typo.Enabled);
        Assert.Contains("no usable address", string.Join(" ", subnet.Notices.Concat(typo.Notices)));

        Assert.True(oneBadOneGood.Enabled);
        Assert.Single(oneBadOneGood.Options.KnownProxies);
        Assert.Contains("only single addresses are supported", oneBadOneGood.Notices.Single());
    }
}

/// <summary>
/// The pooled Neon URL is rewritten before Npgsql sees it, so a missing setting
/// here is the difference between a warm connection and a handshake per request.
/// </summary>
public sealed class DatabaseConnectionTests
{
    [Fact]
    public void NeonUrisKeepThePoolWarm()
    {
        var normalized = new Npgsql.NpgsqlConnectionStringBuilder(
            DatabaseConnection.Normalize("postgres://user:pass@ep.example-1.aws.neon.tech:5432/db?sslmode=require"));

        Assert.Equal(Npgsql.SslMode.Require, normalized.SslMode);
        Assert.Equal(20, normalized.MaxPoolSize);
        // Npgsql ships with keepalive off, which lets the pooler drop idle
        // connections unnoticed.
        Assert.Equal(15, normalized.KeepAlive);
    }

    [Fact]
    public void ExplicitUriParametersStillWin()
    {
        var normalized = new Npgsql.NpgsqlConnectionStringBuilder(
            DatabaseConnection.Normalize("postgres://user:pass@ep.example:5432/db?sslmode=disable&connect_timeout=7"));

        Assert.Equal(Npgsql.SslMode.Disable, normalized.SslMode);
        Assert.Equal(7, normalized.Timeout);
    }

    [Fact]
    public void KeyValuePairStringsArePassedThroughUntouched()
    {
        // A hand-written connection string is only tuned by the operator, so
        // web.config and .env need Keepalive themselves.
        const string plain = "Host=localhost;Port=5432;Database=academy;Username=postgres;Password=postgres";

        Assert.Equal(plain, DatabaseConnection.Normalize(plain));
    }
}
