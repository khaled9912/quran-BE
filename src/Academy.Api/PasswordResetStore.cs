using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Academy.Api;

/// <summary>
/// Short-lived, single-use email codes for password reset. Codes are stored as
/// hashes and limited to five attempts, mirroring the phone OTP store.
/// </summary>
public sealed class PasswordResetStore
{
    private sealed record Entry(string CodeHash, int Attempts, DateTimeOffset ExpiresAt, DateTimeOffset SentAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly bool _devMode;
    private readonly string _devCode;

    public PasswordResetStore(IConfiguration configuration, IHostEnvironment environment)
    {
        // A fixed code is only acceptable on a developer machine with an explicit
        // opt-in; both the environment name and the flag must allow it.
        _devMode = environment.IsDevelopment() && configuration.GetValue("PasswordReset:DevMode", false);
        _devCode = configuration["PasswordReset:DevCode"] is { Length: > 0 } code ? code : "8888";
    }

    public bool DevMode => _devMode;

    /// <summary>Returns the code to deliver, or null while the resend cooldown is active.</summary>
    public string? TryIssue(string email)
    {
        if (_devMode) return _devCode;
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(email, out var existing) && existing.SentAt.AddSeconds(60) > now) return null;
        // Entries only leave on a verify or consume, so an attacker requesting
        // codes for many addresses would grow this dictionary without bound.
        if (_entries.Count >= 500) PurgeExpired(now);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _entries[email] = new Entry(Hash(email, code), 0, now.AddMinutes(15), now);
        return code;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
            if (pair.Value.ExpiresAt < now) _entries.TryRemove(pair.Key, out _);
    }

    public bool Check(string email, string code)
    {
        if (_devMode) return string.Equals(code, _devCode, StringComparison.Ordinal);
        if (!_entries.TryGetValue(email, out var entry)) return false;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow || entry.Attempts >= 5)
        {
            _entries.TryRemove(email, out _);
            return false;
        }
        if (FixedTimeEquals(entry.CodeHash, Hash(email, code))) return true;
        _entries[email] = entry with { Attempts = entry.Attempts + 1 };
        return false;
    }

    public void Consume(string email)
    {
        if (!_devMode) _entries.TryRemove(email, out _);
    }

    private static string Hash(string email, string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{email}:{code}")));

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
