namespace Academy.Api;

/// <summary>
/// Public self-service registration is limited to English-speaking countries.
/// Accounts created by staff are not affected by this policy.
/// </summary>
public static class RegistrationPolicy
{
    private const string CountryHeader = "x-registration-country";

    private static readonly HashSet<string> AllowedCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "GB", "CA", "AU", "NZ", "IE",
    };

    public const string BlockedMessage =
        "Registration is currently available only to students in the United States, United Kingdom, Canada, Australia, New Zealand, and Ireland.";

    /// <summary>
    /// The edge-supplied header wins over the request body. A missing country
    /// fails open so local development and self-hosted previews stay usable.
    /// </summary>
    public static bool IsAllowed(HttpContext http, string? submittedCountry)
    {
        var country = Normalize(http.Request.Headers[CountryHeader].ToString())
            ?? Normalize(submittedCountry);
        return country is null || AllowedCountries.Contains(country);
    }

    private static string? Normalize(string? value)
    {
        var code = value?.Trim().ToUpperInvariant() ?? "";
        return code.Length == 2 && code != "XX" ? code : null;
    }
}
