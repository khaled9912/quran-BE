using System.Net.Http.Json;
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
            var payload = valid.Select(to => new
            {
                to,
                title,
                body,
                sound = "default",
                data = link is null ? null : new { link },
            });
            using var response = await client.PostAsJsonAsync(ExpoEndpoint, payload, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Expo push returned {Status}.", (int)response.StatusCode);
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
public static class LessonOpsMessaging
{
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
        CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var tokens = await db.DeviceTokens.AsNoTracking()
            .Where(x => ids.Contains(x.UserId))
            .Select(x => x.Token)
            .ToListAsync(ct);
        if (tokens.Count == 0) return;
        await push.SendAsync(tokens, title, body, link, ct);
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
