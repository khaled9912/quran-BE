namespace Academy.Api;

/// <summary>The person the provider must invoice, as its API names them.</summary>
public sealed record CheckoutPayer(
    string? Name,
    string? Email,
    string? Country);

/// <summary>
/// What the academy asks a provider to charge. The amount and currency leave the
/// server already final: a browser never gets to propose either.
/// </summary>
public sealed record CheckoutRequest(
    int AmountCents,
    string Currency,
    string MerchantReference,
    string Description,
    CheckoutPayer Payer);

/// <summary>
/// What a browser needs to finish a payment. It carries no credential the academy
/// owns: the client secret only unlocks one checkout session and expires with it,
/// and the publishable key is the only key Stripe allows in front-end code.
/// </summary>
public sealed record CheckoutTicket(
    bool Accepted,
    string? ClientSecret,
    string? PublicKey,
    string ProviderReference,
    string? Error);

/// <summary>
/// The provider's own account of one checkout, read back from its API.
///
/// <see cref="PresentmentAmountCents"/> and <see cref="PresentmentCurrency"/> are
/// what the cardholder was actually asked to pay when the session was priced in
/// one currency and charged in another. They are history, never a check: the
/// verification the invoice depends on is always against the academy's own
/// <see cref="Currency"/> and <see cref="AmountCents"/>.
/// </summary>
public sealed record ProviderCheckoutState(
    bool Found,
    string Status,
    bool Paid,
    string? ProviderTransactionId,
    int AmountCents,
    string Currency,
    string? PaymentBrand,
    string? PaymentMethodType,
    int? PresentmentAmountCents,
    string? PresentmentCurrency,
    string? Error)
{
    /// <summary>
    /// Lets a live checkout be re-opened instead of replaced. A second session for
    /// one invoice is how a student pays the same month twice, so an attempt that
    /// is still open must be handed back rather than abandoned.
    /// </summary>
    public string? ClientSecret { get; init; }

    public static ProviderCheckoutState Unavailable(string error) =>
        new(false, "unknown", false, null, 0, "", null, null, null, null, error);
}

public sealed record ProviderRefundResult(bool Accepted, string? ProviderRefundId, string? Error);

/// <summary>
/// One event Stripe pushed at us, after the signature check. Only the identity of
/// the session is taken on trust from here: the endpoint re-reads the session from
/// the provider's API before it changes an invoice.
/// </summary>
public sealed record ProviderCallback(
    bool Verified,
    string? EventId,
    string EventType,
    string? ProviderReference,
    // The academy's own reference, as the provider echoes it back.
    string? MerchantReference,
    /// <summary>False when the provider sent this from its test environment.</summary>
    bool LiveMode,
    string? Error);

/// <summary>
/// The gateway-agnostic contract the billing endpoints talk to.
///
/// <see cref="IsConfigured"/> is what keeps the feature inert on a host without
/// credentials rather than half-working, and <see cref="SupportsCurrency"/> is what
/// makes a second currency a configuration change instead of a code change.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    bool IsConfigured { get; }

    /// <summary>
    /// The one key this provider allows into a browser. It is handed back on every
    /// ticket, including a reused checkout, so no page has to be told the gateway's
    /// identity separately.
    /// </summary>
    string? PublicKey { get; }

    bool SupportsCurrency(string currency);
    Task<CheckoutTicket> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);
    Task<ProviderCheckoutState> QueryCheckoutAsync(string providerReference, CancellationToken cancellationToken);
    Task<ProviderRefundResult> RefundAsync(string providerTransactionId, int amountCents, string idempotencyKey, CancellationToken cancellationToken);
    ProviderCallback DecodeCallback(string rawBody, string? signatureHeader, DateTimeOffset receivedAt);
}

/// <summary>Standing in for a gateway that has not been configured on this host.</summary>
public sealed class UnconfiguredPaymentProvider(ILogger<UnconfiguredPaymentProvider> logger) : IPaymentProvider
{
    public string Name => "none";
    public bool IsConfigured => false;
    public string? PublicKey => null;
    public bool SupportsCurrency(string currency) => false;

    public Task<CheckoutTicket> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        logger.LogWarning("A payment was requested but no provider is configured on this host.");
        return Task.FromResult(new CheckoutTicket(
            false, null, null, "",
            "Card payment is not available yet. Contact the academy to pay by transfer."));
    }

    public Task<ProviderCheckoutState> QueryCheckoutAsync(string providerReference, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderCheckoutState.Unavailable("No payment provider is configured."));

    public Task<ProviderRefundResult> RefundAsync(string providerTransactionId, int amountCents, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderRefundResult(false, null, "No payment provider is configured."));

    public ProviderCallback DecodeCallback(string rawBody, string? signatureHeader, DateTimeOffset receivedAt) =>
        new(false, null, "", null, null, false, "No payment provider is configured.");
}
