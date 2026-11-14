using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Academy.Api;

/// <summary>
/// Stripe credentials and the currencies the academy is willing to charge in.
///
/// Every value here except the publishable key is a secret and only ever read from
/// the host environment. Processing currency and settlement currency are different
/// things: <see cref="Currencies"/> decides what a cardholder is charged in, and
/// says nothing about the currency that reaches the academy's bank account.
///
/// Only two-decimal currencies belong in <see cref="Currencies"/>. Amounts travel
/// as minor units everywhere in this codebase, and a zero-decimal currency such as
/// JPY would need its own exponent table before it could be offered safely.
/// </summary>
public sealed class StripeOptions(
    string baseUrl,
    string secretKey,
    string publishableKey,
    string webhookSigningSecret,
    string publicBaseUrl,
    IReadOnlyList<string> currencies,
    int checkoutLifetimeMinutes)
{
    public string BaseUrl { get; } = baseUrl;
    public string SecretKey { get; } = secretKey;
    public string PublishableKey { get; } = publishableKey;
    public string WebhookSigningSecret { get; } = webhookSigningSecret;
    public string PublicBaseUrl { get; } = publicBaseUrl;
    public IReadOnlyList<string> Currencies { get; } = currencies;
    public int CheckoutLifetimeMinutes { get; } = checkoutLifetimeMinutes;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey)
        && !string.IsNullOrWhiteSpace(PublishableKey)
        && !string.IsNullOrWhiteSpace(WebhookSigningSecret)
        && Currencies.Count > 0;

    /// <summary>
    /// Which environment this host talks to, read off the key rather than a
    /// setting: a wrong flag here is the difference between a real charge and a
    /// simulated one.
    /// </summary>
    public bool IsLiveCredentials => SecretKey.StartsWith("sk_live", StringComparison.Ordinal);

    public bool SupportsCurrency(string currency) =>
        !string.IsNullOrWhiteSpace(currency)
        && Currencies.Contains(currency.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    public static StripeOptions From(IConfiguration configuration)
    {
        var currencies = (configuration["Stripe:Currencies"] ?? "USD,GBP")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant())
            .Where(c => c.Length == 3)
            .Distinct()
            .ToList();

        // The checkout must land back on the site that started it, so the return
        // url comes from the origin already trusted for CORS unless overridden.
        var publicBaseUrl = configuration["Stripe:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            publicBaseUrl = (configuration["AllowedOrigins"] ?? "http://localhost:3000")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "http://localhost:3000";

        var lifetime = configuration.GetValue("Stripe:CheckoutLifetimeMinutes", 30);

        return new StripeOptions(
            string.IsNullOrWhiteSpace(configuration["Stripe:BaseUrl"]) ? "https://api.stripe.com" : configuration["Stripe:BaseUrl"]!.TrimEnd('/'),
            configuration["Stripe:SecretKey"]?.Trim() ?? "",
            configuration["Stripe:PublishableKey"]?.Trim() ?? "",
            configuration["Stripe:WebhookSigningSecret"]?.Trim() ?? "",
            publicBaseUrl.TrimEnd('/'),
            currencies,
            lifetime < 5 || lifetime > 240 ? 30 : lifetime);
    }
}

/// <summary>
/// Stripe's Checkout Sessions API over a plain HttpClient.
///
/// The official SDK is deliberately not referenced: it would add a large
/// dependency to a 256 MB application pool, and only four endpoints are needed.
/// The API surface used here is small enough to read in one pass, which is the
/// same reason the request bodies are built by hand.
/// </summary>
public sealed class StripeProvider(
    IHttpClientFactory httpFactory,
    StripeOptions options,
    ILogger<StripeProvider> logger) : IPaymentProvider
{
    public const string HttpClientName = "stripe";

    public string Name => "stripe";
    public bool IsConfigured => options.IsConfigured;
    public string? PublicKey => IsConfigured ? options.PublishableKey : null;
    public bool SupportsCurrency(string currency) => options.SupportsCurrency(currency);

    // The key decides which environment this host is, so an event claiming the
    // other one can be refused without a second lookup.
    private bool IsLiveCredentials => options.IsLiveCredentials;

    public async Task<CheckoutTicket> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new CheckoutTicket(false, null, null, "",
                "Card payment is not available yet. Contact the academy to pay by transfer.");

        if (!options.SupportsCurrency(request.Currency))
            return new CheckoutTicket(false, null, null, "", $"Payments in {request.Currency} are not enabled.");

        if (request.AmountCents <= 0)
            return new CheckoutTicket(false, null, null, "", "This invoice has nothing to pay.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("ui_mode", "embedded_page"),
            new("redirect_on_completion", "if_required"),
            new("return_url", $"{options.PublicBaseUrl}/student/payments?session_id={{CHECKOUT_SESSION_ID}}"),
            new("client_reference_id", request.MerchantReference),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", request.AmountCents.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", request.Description),
            // The academy prices each currency itself, so Stripe must not re-convert
            // the amount a student was already shown.
            new("adaptive_pricing[enabled]", "false"),
            new("metadata[merchant_reference]", request.MerchantReference),
        };

        if (!string.IsNullOrWhiteSpace(request.Payer.Email)) form.Add(new("customer_email", request.Payer.Email));
        // There is no name field on a Checkout Session, and the academy's own
        // record of the student is what refunds and disputes are answered from.
        if (!string.IsNullOrWhiteSpace(request.Payer.Name))
            form.Add(new("metadata[payer_name]", request.Payer.Name));
        if (!string.IsNullOrWhiteSpace(request.Payer.Country))
            form.Add(new("metadata[payer_country]", request.Payer.Country));
        if (options.CheckoutLifetimeMinutes > 0)
            form.Add(new("expires_at", DateTimeOffset.UtcNow.AddMinutes(options.CheckoutLifetimeMinutes)
                .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        using var response = await PostAsync("/v1/checkout/sessions", form, request.MerchantReference, cancellationToken);
        var payload = await ReadAsync(response, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new CheckoutTicket(false, null, null, "", FailureMessage(payload, response));

        var id = payload?.RootElement.TryGetString("id");
        var clientSecret = payload?.RootElement.TryGetString("client_secret");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogWarning("Stripe created a checkout session without an id or client secret.");
            return new CheckoutTicket(false, null, null, "", "The payment provider did not return a usable checkout.");
        }

        return new CheckoutTicket(true, clientSecret, options.PublishableKey, id, null);
    }

    public async Task<ProviderCheckoutState> QueryCheckoutAsync(string providerReference, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(providerReference))
            return new ProviderCheckoutState(false, "unknown", false, null, 0, "", null, null, null, null,
                "No payment provider is configured.");

        using var response = await GetAsync(
            $"/v1/checkout/sessions/{Uri.EscapeDataString(providerReference)}?expand[]=payment_intent.payment_method",
            cancellationToken);
        var payload = await ReadAsync(response, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new ProviderCheckoutState(false, "unknown", false, null, 0, "", null, null, null, null,
                "The provider has no record of this checkout.");

        if (!response.IsSuccessStatusCode || payload is null)
            return new ProviderCheckoutState(false, "unknown", false, null, 0, "", null, null, null, null,
                FailureMessage(payload, response));

        var root = payload!.RootElement;
        var paymentIntent = root.TryGetProperty("payment_intent", out var pi) ? pi : default;
        var hasIntent = paymentIntent.ValueKind == JsonValueKind.Object;
        var paymentMethod = hasIntent && paymentIntent.TryGetProperty("payment_method", out var pm) ? pm : default;
        var card = paymentMethod.ValueKind == JsonValueKind.Object && paymentMethod.TryGetProperty("card", out var c) ? c : default;

        var presentment = root.TryGetProperty("presentment_details", out var pd) && pd.ValueKind == JsonValueKind.Object ? pd : default;

        return new ProviderCheckoutState(
            Found: true,
            Status: root.TryGetString("status") ?? "unknown",
            // paid is what Stripe calls the money, and it is the only field here
            // that means the academy may release access.
            Paid: string.Equals(root.TryGetString("payment_status"), "paid", StringComparison.OrdinalIgnoreCase),
            ProviderTransactionId: hasIntent ? paymentIntent.TryGetString("id") : null,
            AmountCents: root.TryGetInt("amount_total") ?? 0,
            Currency: (root.TryGetString("currency") ?? "").ToUpperInvariant(),
            PaymentBrand: card.ValueKind == JsonValueKind.Object ? card.TryGetString("brand") : null,
            PaymentMethodType: paymentMethod.ValueKind == JsonValueKind.Object ? paymentMethod.TryGetString("type") : null,
            PresentmentAmountCents: presentment.ValueKind == JsonValueKind.Object ? presentment.TryGetInt("presentment_amount") : null,
            PresentmentCurrency: presentment.ValueKind == JsonValueKind.Object
                ? presentment.TryGetString("presentment_currency")?.ToUpperInvariant()
                : null,
            Error: null)
        {
            ClientSecret = root.TryGetString("client_secret"),
        };
    }

    public async Task<ProviderRefundResult> RefundAsync(
        string providerTransactionId, int amountCents, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new ProviderRefundResult(false, null, "No payment provider is configured.");
        if (string.IsNullOrWhiteSpace(providerTransactionId)) return new ProviderRefundResult(false, null, "This payment cannot be refunded.");
        if (amountCents <= 0) return new ProviderRefundResult(false, null, "Enter an amount to refund.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", providerTransactionId),
            new("amount", amountCents.ToString(CultureInfo.InvariantCulture)),
        };

        using var response = await PostAsync("/v1/refunds", form, idempotencyKey, cancellationToken);
        var payload = await ReadAsync(response, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new ProviderRefundResult(false, null, FailureMessage(payload, response));

        var root = payload!.RootElement;
        var status = root.TryGetString("status") ?? "pending";
        if (status is "failed" or "canceled")
            return new ProviderRefundResult(false, root.TryGetString("id"), "The provider did not accept the refund.");

        return new ProviderRefundResult(true, root.TryGetString("id"), null);
    }

    /// <summary>
    /// Reads one pushed event, but only after its signature is proven.
    ///
    /// The signature is HMAC-SHA256 over "<c>timestamp</c>.<c>raw body</c>", which
    /// is why the untouched request body is required here. Re-serializing the JSON
    /// first would sign a different string than Stripe did.
    /// </summary>
    public ProviderCallback DecodeCallback(string rawBody, string? signatureHeader, DateTimeOffset receivedAt)
    {
        if (!IsConfigured) return new ProviderCallback(false, null, "", null, null, false, "No payment provider is configured.");
        if (string.IsNullOrWhiteSpace(rawBody) || rawBody.Length > MaxCallbackBytes)
            return new ProviderCallback(false, null, "", null, null, false, "The callback body is too large to accept.");

        if (!StripeSignature.Verify(rawBody, signatureHeader, options.WebhookSigningSecret, receivedAt, out var reason))
        {
            logger.LogInformation("Rejected a payment callback with an unusable signature ({Reason}).", reason);
            return new ProviderCallback(false, null, "", null, null, false, "The callback signature could not be verified.");
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            var session = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("object", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : default;

            var hasSession = session.ValueKind == JsonValueKind.Object;
            var liveMode = root.IsTrue("livemode");

            // A test-environment event must never settle a live invoice, and a live
            // one must never settle a test invoice: both would carry a valid
            // signature, because the same endpoint secret signs for both modes.
            if (liveMode != IsLiveCredentials)
                return new ProviderCallback(false, root.TryGetString("id"), "", null, null, liveMode,
                    "The callback came from the provider's other environment.");

            return new ProviderCallback(
                Verified: true,
                EventId: root.TryGetString("id"),
                EventType: root.TryGetString("type") ?? "",
                ProviderReference: hasSession ? session.TryGetString("id") : null,
                // The academy's own reference, sent with the session and echoed back.
                MerchantReference: hasSession ? session.TryGetString("client_reference_id") : null,
                LiveMode: liveMode,
                Error: null);
        }
        catch (JsonException)
        {
            return new ProviderCallback(false, null, "", null, null, false, "The callback body was not readable.");
        }
    }

    /// <summary>Sized to a checkout event, not a card token: Stripe payloads are a few kilobytes.</summary>
    public const int MaxCallbackBytes = 256 * 1024;

    private async Task<HttpResponseMessage> PostAsync(
        string path, List<KeyValuePair<string, string>> form, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl + path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Length > 255 ? idempotencyKey[..255] : idempotencyKey);

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        return await client.GetAsync(options.BaseUrl + path, cancellationToken);
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient(HttpClientName);
        // The bearer token is the account's secret key and never leaves the server.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        return client;
    }

    private static async Task<JsonDocument?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A short, safe reason for a failed call. Stripe's own message is kept out of
    /// it: provider text can name card fragments, and none of it belongs in a log
    /// or in front of a student.
    /// </summary>
    private string FailureMessage(JsonDocument? payload, HttpResponseMessage response)
    {
        if (payload is not null)
        {
            var code = payload!.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? error.TryGetString("code")
                : null;
            if (!string.IsNullOrWhiteSpace(code))
                logger.LogInformation("Stripe rejected the call with code {Code} ({Status}).", code, (int)response.StatusCode);
        }

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "The payment provider refused the request. Please try again.",
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                => "The payment provider did not recognise the academy's credentials.",
            System.Net.HttpStatusCode.NotFound => "The payment provider has no record of this payment.",
            _ => "The payment provider is unavailable right now. Please try again.",
        };
    }
}

/// <summary>
/// Stripe's webhook signature scheme: <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>,
/// where the hex is HMAC-SHA256 of "<c>t</c>.<c>body</c>" under the endpoint secret.
/// </summary>
public static class StripeSignature
{
    /// <summary>How far a callback may claim to have been signed in the past.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool Verify(string body, string? header, string secret, DateTimeOffset receivedAt, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret))
        {
            reason = "missing signature";
            return false;
        }

        long? timestamp = null;
        var candidates = new List<string>();

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator];
            var value = part[(separator + 1)..];

            if (key == "t" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                timestamp = seconds;
            else if (key == "v1" && !string.IsNullOrWhiteSpace(value))
                candidates.Add(value.Trim().ToLowerInvariant());
        }

        if (timestamp is null || candidates.Count == 0)
        {
            reason = "unreadable signature";
            return false;
        }

        // Replay protection: an old signed body is still a validly signed body, so
        // the timestamp has to be close to now as well as matching the mac.
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (receivedAt - signedAt > Tolerance || signedAt - receivedAt > Tolerance)
        {
            reason = "stale timestamp";
            return false;
        }

        var expected = Compute($"{timestamp}.{body}", secret);
        // Every v1 is compared, and compared without early exit, because Stripe
        // publishes two keys during a secret rotation.
        var matched = false;
        foreach (var candidate in candidates)
            matched |= FixedTimeEquals(candidate, expected);

        if (!matched) reason = "signature mismatch";
        return matched;
    }

    public static string Compute(string payload, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

internal static class JsonReading
{
    public static string? TryGetString(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static int? TryGetInt(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    /// <summary>An absent or non-boolean value reads as false, which is the safe answer.</summary>
    public static bool IsTrue(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;
}
