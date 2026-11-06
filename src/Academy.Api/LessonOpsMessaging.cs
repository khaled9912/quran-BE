using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>Server-side Expo push delivery for lesson notifications.</summary>
public sealed class AcademyPushSender(IHttpClientFactory factory, ILogger<AcademyPushSender> logger)
{
    private const string ExpoEndpoint = "https://exp.host/--/api/v2/push/send";

    public async Task SendAsync(
        IReadOnlyCollection<string> tokens,
        string title,
        string body,
        string? link,
        PushOptions? options = null,
        CancellationToken ct = default)
    {
        var valid = tokens
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 200)
            .Where(t => t.StartsWith("ExponentPushToken[") || t.StartsWith("ExpoPushToken["))
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToList();
        if (valid.Count == 0) return;

        try
        {
            var client = factory.CreateClient("expo");
            var payload = valid.Select(to =>
            {
                var message = new Dictionary<string, object?>
                {
                    ["to"] = to,
                    ["title"] = title,
                    ["body"] = body,
                    ["sound"] = options?.Sound ?? "default",
                };
                if (options?.ChannelId is { Length: > 0 } channel) message["channelId"] = channel;
                if (options?.Priority is { Length: > 0 } priority) message["priority"] = priority;
                if (options?.InterruptionLevel is { Length: > 0 } level) message["interruptionLevel"] = level;
                if (options?.Tag is { Length: > 0 } tag) message["tag"] = tag;
                if (options?.TtlSeconds is int ttl) message["ttl"] = ttl;
                if (options?.Badge is int badge) message["badge"] = badge;
                if (link is not null) message["data"] = new { link };
                return message;
            });
            using var response = await client.PostAsJsonAsync(ExpoEndpoint, payload, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Expo push returned {Status}: {Body}.", (int)response.StatusCode, text);
                return;
            }

            // Expo accepts the request even when it rejects the token, so the
            // per-ticket status is the only place a bad build shows up.
            using var json = JsonDocument.Parse(text);
            if (!json.RootElement.TryGetProperty("data", out var data)) return;
            foreach (var group in data.EnumerateArray())
            {
                if (!group.TryGetProperty("tickets", out var tickets)) continue;
                foreach (var ticket in tickets.EnumerateArray())
                {
                    if (ticket.TryGetProperty("status", out var status)
                        && status.GetString() == "error")
                    {
                        logger.LogWarning("Expo rejected a push token: {Ticket}.", ticket.GetRawText());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Push is best-effort; the in-app notification is already stored.
            logger.LogWarning(ex, "Expo push delivery failed.");
        }
    }
}

/// <summary>
/// Small helpers shared by the lesson-operations endpoints and the background
/// monitor. They never call SaveChanges; the caller owns the transaction.
/// </summary>
/// <summary>
/// How hard a push hits the device. Android volume, vibration and lockscreen
/// visibility live on the named channel, which the app registers at startup;
/// a plain push can never change them. The default channel id must match
/// DEFAULT_NOTIFICATION_CHANNEL_ID in the mobile app.
/// </summary>
public sealed record PushOptions(
    string Sound = "default",
    string ChannelId = "academy-notifications",
    string? Priority = null,
    string? InterruptionLevel = null,
    string? Tag = null,
    int? TtlSeconds = null,
    int? Badge = null);

public static class LessonOpsMessaging
{
    /// <summary>
    /// A lesson call must be heard in a pocket, so it is delivered on its own
    /// loud channel, expires within a minute, and replaces any older ring
    /// rather than stacking up behind it.
    ///
    /// The channel id has to match the one the apps create themselves, and
    /// Android refuses to reconfigure a channel that already exists — changing
    /// the client-side settings therefore means bumping this id too.
    /// </summary>
    public static readonly PushOptions UrgentCall = new(
        Sound: "lesson_call.wav",
        ChannelId: "lesson-calls-2",
        Priority: "high",
        InterruptionLevel: "time-sensitive",
        Tag: "lesson-call",
        TtlSeconds: 60);

    public static AppNotification Notice(
        Guid userId,
        string title,
        string body,
        string kind,
        string? link,
        string? dedupeKey) => new()
        {
            UserId = userId,
            Title = title.Length > 160 ? title[..160] : title,
            Body = body.Length > 500 ? body[..500] : body,
            Kind = kind,
            Link = link,
            DedupeKey = dedupeKey is { Length: > 160 } ? dedupeKey[..160] : dedupeKey,
        };

    /// <summary>
    /// The line staff need before they can act on a lesson notice: who the
    /// lesson was between, when it ran, and what happened. Pair it with
    /// <see cref="LessonLink"/> so the notification opens that exact lesson.
    /// </summary>
    public static string LessonSummary(Lesson lesson, string detail) =>
        $"{lesson.Teacher?.FullName ?? "The teacher"} with " +
        $"{lesson.Student?.FullName ?? "the student"}, " +
        $"{When(lesson.StartsAt, lesson.Series?.TimeZoneId)}. {detail}";

    /// <summary>
    /// A moment written into notification copy. The body is plain text, so the
    /// instant is rendered on the clock the slot was booked on — the teacher's —
    /// and that clock is named, because a student reads the same sentence while
    /// their own device shows the lesson on theirs.
    /// </summary>
    public static string When(DateTimeOffset instant, string? timeZoneId) =>
        $"{Localize(instant, timeZoneId).ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.InvariantCulture)}{ZoneSuffix(timeZoneId)}";

    /// <summary>A recurring weekly slot as the teacher publishes it.</summary>
    public static string WeeklyAt(int dayOfWeek, TimeOnly startsAt, string? timeZoneId) =>
        $"{(DayOfWeek)dayOfWeek} at {startsAt.ToString("h:mm tt", CultureInfo.InvariantCulture)}{ZoneSuffix(timeZoneId)}";

    private static DateTimeOffset Localize(DateTimeOffset instant, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return instant.ToUniversalTime();
        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? TimeZoneInfo.ConvertTime(instant, zone)
            : instant.ToUniversalTime();
    }

    private static string ZoneSuffix(string? timeZoneId)
    {
        var id = timeZoneId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return " UTC time";
        return $" {id.Split('/')[^1].Replace('_', ' ')} time";
    }

    /// <summary>
    /// The lesson page exists on both the web app and the mobile app, and an
    /// admin opening it sees the room and the attendance panel for that lesson.
    /// </summary>
    public static string LessonLink(Lesson lesson) => $"/lesson/{lesson.Id}";

    public static async Task<List<Guid>> AdminUserIdsAsync(AcademyDbContext db, CancellationToken ct = default)
    {
        var roleIds = db.Roles
            .Where(r => r.NormalizedName == "ADMIN" || r.NormalizedName == "SUPER_ADMIN")
            .Select(r => r.Id);
        return await db.UserRoles
            .Where(x => roleIds.Contains(x.RoleId))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public static async Task PushAsync(
        AcademyDbContext db,
        AcademyPushSender push,
        IEnumerable<Guid> userIds,
        string title,
        string body,
        string? link,
        CancellationToken ct = default,
        PushOptions? options = null)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var tokens = await db.DeviceTokens.AsNoTracking()
            .Where(x => ids.Contains(x.UserId))
            .Select(x => new { x.UserId, x.Token })
            .ToListAsync(ct);
        if (tokens.Count == 0) return;

        // The badge is the reader's unread count, so the icon matches the tray
        // even when the app is closed. iOS applies the number itself; Android
        // shows a dot from the channel, and the app keeps the count in step.
        var unread = await db.Notifications.AsNoTracking()
            .Where(x => ids.Contains(x.UserId) && x.ReadAt == null)
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var messageOptions = options ?? new PushOptions();
        foreach (var group in tokens.GroupBy(x => x.UserId))
            await push.SendAsync(
                group.Select(x => x.Token).ToList(), title, body, link,
                messageOptions with { Badge = unread.GetValueOrDefault(group.Key) }, ct);
    }

    public static async Task EmailAsync(
        AcademyEmailSender email,
        AcademyUser? user,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        if (user?.Email is not { Length: > 3 } address) return;
        if (!email.IsConfigured) return;
        await email.SendNoticeAsync(address, subject, body, ct);
    }

    public static async Task EmailAdminsAsync(
        AcademyDbContext db,
        AcademyEmailSender email,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        if (!email.IsConfigured) return;
        var ids = await AdminUserIdsAsync(db, ct);
        var recipients = await db.Users.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.Email != null)
            .Select(x => x.Email!)
            .ToListAsync(ct);
        foreach (var address in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
            await email.SendNoticeAsync(address, subject, body, ct);
    }
}
