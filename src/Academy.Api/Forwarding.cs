using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Academy.Api;

/// <summary>
/// The web app reaches the API through a same-origin proxy, so every browser
/// arrives from one of the proxy's egress addresses and the real client only
/// appears in X-Forwarded-For. That header is attacker-controlled, so it is
/// honoured solely for peers listed in Forwarding:TrustedProxies. With no list
/// configured the middleware is never registered and the connection address
/// remains the only thing the app trusts.
/// </summary>
public static class Forwarding
{
    /// <summary>
    /// Registers the middleware options and returns whether forwarded headers are
    /// trusted. Anything written to <paramref name="problem"/> is a configuration
    /// mistake the operator has to see at startup.
    /// </summary>
    public static bool TryConfigure(IServiceCollection services, IConfiguration config, Action<string> problem)
    {
        var proxies = Split(config["Forwarding:TrustedProxies"]);
        if (proxies.Count == 0) return false;

        var hosts = Split(config["Forwarding:AllowedHosts"]);
        var trusted = new List<IPAddress>();
        foreach (var proxy in proxies)
        {
            // A slash must be a /32 host address; subnets are refused rather than
            // silently widened, because a typo here decides who may name a client.
            var parts = proxy.Split('/');
            if (parts.Length == 2 && parts[1] is not ("32" or "128"))
            {
                problem($"Ignoring Forwarding:TrustedProxies entry \"{proxy}\": only single addresses are supported.");
                continue;
            }
            if (!IPAddress.TryParse(parts[0], out var address))
            {
                problem($"Ignoring Forwarding:TrustedProxies entry \"{proxy}\": it is not an IP address.");
                continue;
            }
            trusted.Add(address);
        }

        if (trusted.Count == 0)
        {
            problem("Forwarding:TrustedProxies contained no usable address, so forwarded headers stay disabled.");
            return false;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The framework's loopback defaults cannot match a hosted proxy, and a
            // leftover default would widen trust beyond the configured list.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in trusted) options.KnownProxies.Add(address);

            // X-Forwarded-Host names the public origin, so a client could otherwise
            // point realtime clients at a host of its choosing. Only rewrite it when
            // the operator has said which hosts are valid.
            if (hosts.Count > 0)
            {
                options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
                options.AllowedHosts.Clear();
                foreach (var host in hosts) options.AllowedHosts.Add(host);
            }
        });

        return true;
    }

    private static List<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
