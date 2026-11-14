using System.Text.Json;
using Academy.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Academy.SecurityTests.Infrastructure;

/// <summary>
/// A checkout provider the tests can drive by hand.
///
/// Only the network calls are faked. Signature checking and event decoding go
/// through the real <see cref="StripeProvider"/>, because those are the parts a
/// payment is settled on and a stub would let a broken verifier pass.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    /// <summary>The endpoint secret both the tests and this provider sign with.</summary>
    public const string WebhookSecret = "whsec_test_0123456789abcdef";

    private const string SecretKey = "sk_test_local";

    private readonly StripeProvider _decoder;
    private int _sessions;

    public FakePaymentProvider()
    {
        var options = new StripeOptions(
            "https://stripe.test",
            SecretKey,
            "pk_test_local",
            WebhookSecret,
            "https://localhost:3000",
            ["USD", "GBP"],
            30);
        _decoder = new StripeProvider(new UnusedHttpFactory(), options, NullLogger<StripeProvider>.Instance);
    }

    /// <summary>Every checkout the API asked for, in order.</summary>
    public List<CheckoutRequest> Requests { get; } = [];

    /// <summary>The sessions the API may read back, keyed by provider reference.</summary>
    public Dictionary<string, ProviderCheckoutState> Sessions { get; } = [];

    /// <summary>Refunds the desk has been allowed to send.</summary>
    public List<(string TransactionId, int AmountCents, string Key)> Refunds { get; } = [];

    /// <summary>Set false to make the provider refuse to open a checkout.</summary>
    public bool AcceptsCheckout { get; set; } = true;

    public string Name => "stripe";
    public bool IsConfigured => true;
    public string? PublicKey => "pk_test_local";
    public bool SupportsCurrency(string currency) => currency is "USD" or "GBP";

    public Task<CheckoutTicket> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (!AcceptsCheckout)
            return Task.FromResult(new CheckoutTicket(false, null, null, "", "The payment provider refused."));

        var reference = $"cs_{Interlocked.Increment(ref _sessions):D3}";
        Sessions[reference] = Open(reference, request.AmountCents, request.Currency);

        return Task.FromResult(new CheckoutTicket(
            true, SecretOf(reference), PublicKey, reference, null));
    }

    public Task<ProviderCheckoutState> QueryCheckoutAsync(string providerReference, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.TryGetValue(providerReference, out var state)
            ? state
            : ProviderCheckoutState.Unavailable("no such session"));

    public Task<ProviderRefundResult> RefundAsync(
        string providerTransactionId, int amountCents, string idempotencyKey, CancellationToken cancellationToken)
    {
        Refunds.Add((providerTransactionId, amountCents, idempotencyKey));
        return Task.FromResult(new ProviderRefundResult(true, $"re_{Refunds.Count}", null));
    }

    public ProviderCallback DecodeCallback(string rawBody, string? signatureHeader, DateTimeOffset receivedAt) =>
        _decoder.DecodeCallback(rawBody, signatureHeader, receivedAt);

    /// <summary>What a live checkout looks like before anyone pays.</summary>
    public static ProviderCheckoutState Open(string reference, int amountCents, string currency) =>
        new(true, "open", false, null, amountCents, currency, null, null, null, null, null)
        {
            ClientSecret = SecretOf(reference),
        };

    /// <summary>The same secret the embedded checkout was handed when the session opened.</summary>
    private static string SecretOf(string reference) => $"secret_{reference}";

    /// <summary>Moves a session to paid, as the provider would after a card is taken.</summary>
    public void MarkPaid(
        string reference, int? amountCents = null, string? currency = null, string? transactionId = null,
        int? presentmentCents = null, string? presentmentCurrency = null)
    {
        var state = Sessions[reference];
        Sessions[reference] = state with
        {
            Status = "complete",
            Paid = true,
            ProviderTransactionId = transactionId ?? $"pi_{reference}",
            PaymentBrand = "visa",
            PaymentMethodType = "card",
            AmountCents = amountCents ?? state.AmountCents,
            Currency = currency ?? state.Currency,
            PresentmentAmountCents = presentmentCents,
            PresentmentCurrency = presentmentCurrency,
        };
    }

    public void MarkExpired(string reference) =>
        Sessions[reference] = Sessions[reference] with { Status = "expired", Paid = false };

    /// <summary>Drops a session, the way a provider outage makes every read fail.</summary>
    public void Forget(string reference) => Sessions.Remove(reference);

    /// <summary>A signed Stripe-shaped event body, exactly as the endpoint would post it.</summary>
    public static string CompletedEvent(
        string eventId,
        string sessionId,
        string? merchantReference,
        bool liveMode = false,
        string type = "checkout.session.completed") =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type,
            livemode = liveMode,
            data = new
            {
                @object = new Dictionary<string, object?>
                {
                    ["id"] = sessionId,
                    ["client_reference_id"] = merchantReference,
                    ["payment_status"] = "paid",
                },
            },
        });

    public static string SignedWith(string body, DateTimeOffset at) =>
        $"t={at.ToUnixTimeSeconds()},v1={StripeSignature.Compute($"{at.ToUnixTimeSeconds()}.{body}", WebhookSecret)}";

    /// <summary>The provider never opens a socket here; the decoder only needs a factory to exist.</summary>
    private sealed class UnusedHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
