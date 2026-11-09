using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPush;
using WebPush.Model;

namespace Academy.Api;

/// <summary>
/// Delivers a message to browsers the user subscribed for Web Push, so a lesson
/// call can reach a student whose laptop has no tab open. Expo push covers the
/// phone; this covers everything else, and the two are tried one after the other
/// from LessonOpsMessaging.PushAsync.
///
/// The library performs the AES-GCM sealing and the VAPID signature; the
/// private key never leaves configuration, and only the public half is ever
/// handed to a client.
///
/// It is a singleton that takes the caller's DbContext as an argument, exactly
/// like the static helpers in LessonOpsMessaging: the subscription cleanup
/// below is the only reason it touches the database, and the caller owns the
/// scope either way.
/// </summary>
public sealed class WebPushSender(IConfiguration configuration, IHttpClientFactory factory, ILogger<WebPushSender> logger)
{
    private readonly string? _publicKey = configuration["WebPush:PublicKey"];
    private readonly string? _privateKey = configuration["WebPush:PrivateKey"];
    private readonly string _subject = configuration["WebPush:Subject"] ?? "mailto:support@anaquran.com";

    /// <summary>False until both halves of the VAPID pair are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_publicKey) && !string.IsNullOrWhiteSpace(_privateKey);

    /// <summary>
    /// The key the browser needs to create a subscription. It is not secret —
    /// it identifies this application server to the push service.
    /// </summary>
    public string? PublicKey => IsConfigured ? _publicKey : null;

    public async Task SendAsync(
        AcademyDbContext db,
        IReadOnlyCollection<BrowserSubscription> subscriptions,
        string title,
        string body,
        string? link,
        PushOptions? options = null,
        CancellationToken ct = default)
    {
        if (subscriptions.Count == 0) return;
        if (!IsConfigured)
        {
            logger.LogDebug("Web push is not configured; {Count} browser subscription(s) were skipped.", subscriptions.Count);
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title,
            body,
            link,
            // The service worker passes this straight to showNotification, so a
            // second call replaces the first ring instead of stacking banners.
            tag = options?.Tag,
            urgent = options?.Priority == "high",
        });

        using var client = new WebPushClient(factory.CreateClient("webpush"));
        // Carried on the options rather than only on the client, so there is no
        // chance of a send going out unsigned.
        var vapid = new VapidDetails(_subject, _publicKey!, _privateKey!);
        var sendOptions = new WebPushOptions
        {
            VapidDetails = vapid,
            TTL = options?.TtlSeconds ?? 60 * 60 * 24,
            Urgency = options?.Priority == "high" ? Urgency.High : Urgency.Normal,
            // The RFC 8188 coding, named rather than left to the library's
            // default: the older aesgcm scheme is on its way out and Safari
            // will not accept a message sealed with it forever.
            ContentEncoding = ContentEncoding.Aes128gcm,
        };

        // A push service answers 404/410 once a browser forgets, resets, or
        // stops honouring a subscription. Keeping those rows means every later
        // notice wastes a request and the tray never fills up again, so they
        // are dropped as they are found.
        var dead = new List<Guid>();
        foreach (var subscription in subscriptions)
        {
            try
            {
                await client.SendNotificationAsync(
                    new PushSubscription(subscription.Endpoint, subscription.P256Dh, subscription.Auth),
                    payload, sendOptions, ct);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                dead.Add(subscription.Id);
                logger.LogInformation("Web push service dropped subscription {Id} ({Status}).", subscription.Id, (int)ex.StatusCode);
            }
            catch (WebPushException ex)
            {
                // 429 and 4xx from a rate-limited push service are temporary, so
                // the subscription stays; the in-app notice already landed.
                logger.LogWarning(ex, "Web push delivery failed with {Status}.", (int)ex.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Web push delivery failed.");
            }
        }

        if (dead.Count > 0)
            await db.BrowserSubscriptions.Where(x => dead.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }
}
