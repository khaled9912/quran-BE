using System.Net;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The callback Stripe posts. What arrives from the network is a hint that
/// something happened, never the thing itself: every path below has to survive
/// the session being read back from the provider and compared with the invoice
/// this server issued.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PaymentCallbackTests(PostgresFixture postgres) : PaymentHarness(postgres)
{
    [Fact]
    public async Task ACallbackWithNoSignatureIsRefusedWithoutATrace()
    {
        var (_, invoiceId, ticket) = await CheckoutAsync();
        PayAtProvider(ticket);
        var body = Event(ticket);

        var response = await SendEventAsync(body, signature: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
        // An unsigned request is logged, not stored: it would otherwise be a cheap
        // way to fill the audit table from outside.
        Assert.Equal(0, await CountAsync(db => db.PaymentCallbackEvents));
    }

    [Fact]
    public async Task ABodyEditedInTransitIsRefused()
    {
        var (_, invoiceId, ticket) = await CheckoutAsync();
        PayAtProvider(ticket);
        var body = Event(ticket);
        var signature = FakePaymentProvider.SignedWith(body, DateTimeOffset.UtcNow);

        var forged = body.Replace($"cs_", "cs_x");
        Assert.NotEqual(body, forged);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendEventAsync(forged, signature)).StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task ACallbackSentLateIsRefused()
    {
        var (_, invoiceId, ticket) = await CheckoutAsync();
        PayAtProvider(ticket);
        var body = Event(ticket);
        var tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendEventAsync(body, FakePaymentProvider.SignedWith(body, tenMinutesAgo))).StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task ACallbackFromTheOtherProviderEnvironmentIsRefused()
    {
        // The same endpoint secret signs for both modes, so the only thing telling
        // them apart is the flag on the event — and a test-mode payment is not money.
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);

        var response = await SendPaidAsync(ticket, liveMode: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal(0, await CountAsync(db => db.PaymentCallbackEvents));
    }

    [Fact]
    public async Task ACallbackAboutACheckoutNobodyOpenedIsLoggedAndIgnored()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);

        var body = FakePaymentProvider.CompletedEvent("evt_stranger", "cs_never_mine", null);
        var response = await SendEventAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal("unmatched", (await CallbackAsync("evt_stranger"))!.Outcome);
    }

    [Fact]
    public async Task ACallbackSettlesTheMonthTheProviderConfirms()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);
        var eventId = $"evt_{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);

        var invoice = await InvoiceAsync(invoiceId);
        Assert.Equal("paid", invoice!.Status);
        Assert.NotNull(invoice.PaidAt);
        Assert.Equal("captured", (await AttemptAsync(AttemptIdOf(ticket))).Status);

        var callback = await CallbackAsync(eventId);
        Assert.Equal("applied", callback!.Outcome);
        Assert.Equal(4400, callback.AmountCents);
        Assert.Equal("USD", callback.Currency);
        Assert.Equal(ReferenceOf(ticket), callback.ProviderCheckoutId);
        Assert.Equal(1, await CountAsync(db => db.AuditLogs.Where(x => x.Action == "payment.captured")));
    }

    [Fact]
    public async Task ARedeliveredCallbackSettlesTheMonthOnce()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);
        var eventId = $"evt_{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);
        var first = await InvoiceAsync(invoiceId);
        var paidAt = first!.PaidAt;

        // Stripe retries until it sees 2xx, so the same event arrives again and again.
        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);

        var again = await InvoiceAsync(invoiceId);
        Assert.Equal("paid", again!.Status);
        Assert.Equal(paidAt, again.PaidAt);
        Assert.Equal(1, await CountAsync(db => db.PaymentCallbackEvents.Where(x => x.ProviderEventId == eventId)));
        Assert.Equal(1, await CountAsync(db => db.Notifications.Where(x => x.DedupeKey == $"payment:{invoiceId}")));
        Assert.Equal(1, await CountAsync(db => db.AuditLogs.Where(x => x.Action == "payment.captured")));
    }

    [Fact]
    public async Task ASessionChargedInAnotherCurrencyDoesNotPayTheInvoice()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkPaid(ReferenceOf(ticket), currency: "GBP");
        var eventId = $"evt_{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);

        Assert.NotEqual("paid", (await InvoiceAsync(invoiceId))!.Status);
        var attempt = await AttemptAsync(AttemptIdOf(ticket));
        Assert.Equal("failed", attempt.Status);
        Assert.Contains("GBP", attempt.FailureReason);
        Assert.Equal("rejected_currency", (await CallbackAsync(eventId))!.Outcome);
    }

    [Fact]
    public async Task ASessionChargedShortOfTheInvoiceDoesNotPayIt()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkPaid(ReferenceOf(ticket), amountCents: 4399);
        var eventId = $"evt_{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);

        Assert.NotEqual("paid", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Null((await InvoiceAsync(invoiceId))!.PaidAt);
        Assert.Equal("rejected_amount", (await CallbackAsync(eventId))!.Outcome);
        Assert.Equal("failed", (await AttemptAsync(AttemptIdOf(ticket))).Status);
    }

    [Fact]
    public async Task AChargedAmountAboveTheInvoiceDoesNotPayItEither()
    {
        // Overpayment is not consent to bill later: the figure must match, not merely
        // be at least the invoice.
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkPaid(ReferenceOf(ticket), amountCents: 4401);
        var eventId = $"evt_{Guid.NewGuid():N}";

        await SendPaidAsync(ticket, eventId);

        Assert.NotEqual("paid", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal("rejected_amount", (await CallbackAsync(eventId))!.Outcome);
    }

    [Fact]
    public async Task AChargeConvertedAtTheCardholderIsSettledInOurOwnCurrency()
    {
        // A cardholder can be shown a converted figure while the academy is paid in
        // the invoice currency. What is recorded is the localisation; what decides
        // the invoice is the amount and currency the academy asked for.
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkPaid(ReferenceOf(ticket), presentmentCents: 3612, presentmentCurrency: "EUR");
        var eventId = $"evt_{Guid.NewGuid():N}";

        await SendPaidAsync(ticket, eventId);

        Assert.Equal("paid", (await InvoiceAsync(invoiceId))!.Status);
        var attempt = await AttemptAsync(AttemptIdOf(ticket));
        Assert.Equal(3612, attempt.PresentmentAmountCents);
        Assert.Equal("EUR", attempt.PresentmentCurrency);
        Assert.Equal(4400, attempt.AmountCents);
        Assert.Equal("USD", attempt.Currency);
        Assert.Equal("applied", (await CallbackAsync(eventId))!.Outcome);
    }

    [Fact]
    public async Task ASecondVerifiedPaymentForAPaidMonthReleasesNothing()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);

        await SendPaidAsync(ticket, "evt_first");
        var paidAt = (await InvoiceAsync(invoiceId))!.PaidAt;

        // Real money arriving twice for one month. The second is recorded for the
        // desk to refund by hand, and nothing is handed out again.
        await SendPaidAsync(ticket, "evt_second");

        var invoice = await InvoiceAsync(invoiceId);
        Assert.Equal("paid", invoice!.Status);
        Assert.Equal(paidAt, invoice.PaidAt);
        Assert.Equal("duplicate", (await CallbackAsync("evt_second"))!.Outcome);
        Assert.Equal(1, await CountAsync(db => db.Notifications.Where(x => x.DedupeKey == $"payment:{invoiceId}")));
    }

    [Fact]
    public async Task ACheckoutThatExpiredBeforeItWasPaidReopensTheMonth()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkExpired(ReferenceOf(ticket));
        var eventId = $"evt_{Guid.NewGuid():N}";

        await SendPaidAsync(ticket, eventId, type: "checkout.session.expired");

        var invoice = await InvoiceAsync(invoiceId);
        Assert.Equal("open", invoice!.Status);
        Assert.Null(invoice.PaidAt);
        Assert.Equal("voided", (await AttemptAsync(AttemptIdOf(ticket))).Status);
        Assert.Equal("ignored", (await CallbackAsync(eventId))!.Outcome);
    }

    [Fact]
    public async Task ACallbackTheProviderCannotConfirmAsksForARetry()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);
        Provider.Forget(ReferenceOf(ticket));
        var eventId = $"evt_{Guid.NewGuid():N}";

        var response = await SendPaidAsync(ticket, eventId);

        // The honest answer is 5xx: the money is not known, so Stripe must send it again.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual("paid", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal("deferred", (await CallbackAsync(eventId))!.Outcome);
    }

    [Fact]
    public async Task AnEventAboutNothingMonetaryIsAcknowledgeAndLeftAlone()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;

        var body = FakePaymentProvider.CompletedEvent(
            "evt_noise", ReferenceOf(ticket), null, type: "product.created");

        Assert.Equal(HttpStatusCode.OK, (await SendEventAsync(body)).StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal(0, await CountAsync(db => db.PaymentCallbackEvents));
    }

    [Fact]
    public async Task ARefundNoticeFromTheProviderDoesNotUndrawAMonthOnItsOwn()
    {
        // Refunding is the desk's decision, taken against the ledger. A provider
        // event alone never rewrites an invoice.
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);
        await SendPaidAsync(ticket, "evt_paid");

        var body = FakePaymentProvider.CompletedEvent(
            "evt_refund_noise", ReferenceOf(ticket), null, type: "charge.refunded");
        Assert.Equal(HttpStatusCode.OK, (await SendEventAsync(body)).StatusCode);

        Assert.Equal("paid", (await InvoiceAsync(invoiceId))!.Status);
    }

    private static string Event(JsonNode ticket) =>
        FakePaymentProvider.CompletedEvent(
            $"evt_{Guid.NewGuid():N}",
            ReferenceOf(ticket),
            $"t{AttemptIdOf(ticket):N}");
}
