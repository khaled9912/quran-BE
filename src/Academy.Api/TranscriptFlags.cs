using System.Text.RegularExpressions;

namespace Academy.Api;

/// <summary>
/// Flags transcript segments that look like an attempt to move a student
/// off-platform. Patterns are deliberately broad: an admin reviews every hit,
/// so a false positive costs a glance while a missed phone number costs a
/// student. Spoken numbers arrive with filler ("zero one zero" stays text and
/// is not matched), so this complements human review, not replaces it.
/// </summary>
public static partial class TranscriptFlags
{
    [GeneratedRegex(@"\+?\d[\d\s().-]{5,}\d")]
    private static partial Regex PhoneLike();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(whats\s?app|telegram|instagram|insta|snapchat|snap chat|tiktok|facebook|messenger|signal|skype)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Platforms();

    [GeneratedRegex(@"\b(call me|text me|dm me|whatsapp me|my number|your number|phone number|off[- ]platform|outside (the )?(lesson|class|platform))\b", RegexOptions.IgnoreCase)]
    private static partial Regex OffPlatform();

    public static IReadOnlyList<string> Scan(string text)
    {
        var found = new List<string>();
        // A date or a lesson time ("2026-11-01", "10:30 to 11:30") has 8 digits;
        // require 9, or 8 behind an explicit +, so schedules are not flagged.
        foreach (Match match in PhoneLike().Matches(text))
        {
            var digits = match.Value.Count(char.IsDigit);
            if (digits >= 9 || (match.Value.TrimStart().StartsWith('+') && digits >= 8))
            {
                found.Add("phone");
                break;
            }
        }
        if (Email().IsMatch(text)) found.Add("email");
        if (Platforms().IsMatch(text)) found.Add("platform");
        if (OffPlatform().IsMatch(text)) found.Add("offplatform");
        return found;
    }
}
