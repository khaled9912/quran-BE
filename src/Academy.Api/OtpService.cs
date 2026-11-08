using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Academy.Api;

public static class PhoneNumbers
{
    /// <summary>Normalizes to E.164-ish "+<digits>" or returns null when unusable.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        return digits.Length is < 8 or > 15 ? null : $"+{digits}";
    }
}

public sealed class PhoneOtpStore
{
    private sealed record Entry(string CodeHash, int Attempts, DateTimeOffset ExpiresAt, DateTimeOffset SentAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly bool _devMode;
    private readonly string _devCode;

    public PhoneOtpStore(IConfiguration configuration, IHostEnvironment environment)
    {
        // A fixed code is only ever acceptable on a developer machine, and only
        // when explicitly opted in. Both gates must hold: a Development environment
        // name AND Otp:DevMode=true. Defaulting the flag to false means a mislabeled
        // or compose-launched "Development" deployment cannot silently accept 8888.
        _devMode = environment.IsDevelopment() && configuration.GetValue("Otp:DevMode", false);
        _devCode = configuration["Otp:DevCode"] is { Length: > 0 } code ? code : "8888";
    }

    public bool DevMode => _devMode;
    public string DevCode => _devCode;

    /// <summary>Returns the code to deliver, or null when the resend cooldown is active.</summary>
    public string? TryIssue(string phone)
    {
        if (_devMode) return _devCode;
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(phone, out var existing) && existing.SentAt.AddSeconds(60) > now) return null;
        // Entries only leave on a verify or consume, so an attacker issuing codes
        // to many numbers would grow this dictionary without bound.
        if (_entries.Count >= 500) PurgeExpired(now);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _entries[phone] = new Entry(Hash(phone, code), 0, now.AddMinutes(5), now);
        return code;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
            if (pair.Value.ExpiresAt < now) _entries.TryRemove(pair.Key, out _);
    }

    public bool Check(string phone, string code)
    {
        if (_devMode) return string.Equals(code, _devCode, StringComparison.Ordinal);
        if (!_entries.TryGetValue(phone, out var entry)) return false;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow || entry.Attempts >= 5)
        {
            _entries.TryRemove(phone, out _);
            return false;
        }
        if (FixedTimeEquals(entry.CodeHash, Hash(phone, code))) return true;
        _entries[phone] = entry with { Attempts = entry.Attempts + 1 };
        return false;
    }

    public void Consume(string phone)
    {
        if (!_devMode) _entries.TryRemove(phone, out _);
    }

    private static string Hash(string phone, string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{phone}:{code}")));

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Phone-only accounts have no email. Identity's default validator reports an empty
/// email as invalid while RequireUniqueEmail is enabled, so email checks are skipped
/// for users created with a phone number.
/// </summary>
public sealed class AcademyUserValidator : UserValidator<AcademyUser>
{
    private readonly IdentityErrorDescriber _describer;

    public AcademyUserValidator(IdentityErrorDescriber describer) : base(describer) => _describer = describer;

    public override async Task<IdentityResult> ValidateAsync(UserManager<AcademyUser> manager, AcademyUser user)
    {
        if (!string.IsNullOrWhiteSpace(await manager.GetEmailAsync(user)))
            return await base.ValidateAsync(manager, user);

        var userName = await manager.GetUserNameAsync(user);
        var allowed = manager.Options.User.AllowedUserNameCharacters;
        if (string.IsNullOrWhiteSpace(userName) || (allowed is not null && userName.Any(c => !allowed.Contains(c))))
            return IdentityResult.Failed(_describer.InvalidUserName(userName));

        var owner = await manager.FindByNameAsync(userName);
        if (owner is not null && !string.Equals(await manager.GetUserIdAsync(owner), await manager.GetUserIdAsync(user), StringComparison.Ordinal))
            return IdentityResult.Failed(_describer.DuplicateUserName(userName));

        return IdentityResult.Success;
    }
}

public interface IOtpSender
{
    bool IsConfigured { get; }
    Task SendAsync(string phone, string code, CancellationToken cancellationToken);
}

/// <summary>Placeholder until a WhatsApp/SMS provider is configured (Otp:Sender).</summary>
public sealed class UnconfiguredOtpSender(ILogger<UnconfiguredOtpSender> logger) : IOtpSender
{
    public bool IsConfigured => false;

    public Task SendAsync(string phone, string code, CancellationToken cancellationToken)
    {
        logger.LogWarning("OTP delivery is not configured; no message was sent to {Phone}.", phone);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Free SMS via TextBee (https://textbee.dev): messages leave through the SIM of an
/// Android phone registered to the account, so there is no per-message cost.
/// </summary>
public sealed class TextBeeOtpSender : IOtpSender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _apiKey;
    private readonly string? _deviceId;

    public TextBeeOtpSender(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["Otp:TextBee:ApiKey"];
        _deviceId = configuration["Otp:TextBee:DeviceId"];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task SendAsync(string phone, string code, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["recipients"] = new[] { phone },
            ["message"] = $"Your Ana Quran Academy verification code is {code}. It expires in 5 minutes."
        };
        if (!string.IsNullOrWhiteSpace(_deviceId)) payload["deviceId"] = _deviceId;

        var client = _httpClientFactory.CreateClient("otp");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.textbee.dev/api/v1/gateway/send-sms")
        {
            Content = JsonContent.Create(payload),
            Headers = { { "x-api-key", _apiKey! } }
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TextBee returned {(int)response.StatusCode}.");
    }
}
