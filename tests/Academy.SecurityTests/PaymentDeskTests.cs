using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The desk's side of card payments: the price list every invoice is drawn from,
/// the ledger of attempts and callbacks, and the only route money goes back out.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PaymentDeskTests(PostgresFixture postgres) : PaymentHarness(postgres)
{
    private static readonly (int Days, decimal Gbp)[] GbpPrices =
    [
        (2, 30.00m),
        (3, 34.00m),
        (4, 38.00m),
        (5, 42.00m),
    ];

    [Fact]
    public async Task OnlyTheDeskCanReadThePaymentLedger()
    {
        var (student, _, _) = await BilledAsync();
        using var _ = student;
        var teacher = await Factory.LoginAsync("teacher");
        using var __ = teacher;
        var admin = await Factory.LoginAsync("admin");
        using var ___ = admin;

        foreach (var path in new[] { "/prices", "/invoices", "/transactions", "/callbacks" })
        {
            var url = $"/api/admin/payments{path}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.Client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await teacher.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
        }

        // Writing a price is the same door, and no door other than the desk's.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await student.PutAsJsonAsync("/api/admin/payments/prices",
                new { daysPerWeek = 2, currency = "USD", monthlyAmount = 1m })).StatusCode);
    }

    [Fact]
    public async Task TheDeskIsToldWhichTiersAndCurrenciesItMayPrice()
    {
        // The price desk edits what the server can honour, so the choices it offers
        // come from the server: the published tiers and the charge currencies the
        // payment account accepts. A currency with no rows is listed but unpriced.
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;

        var board = await admin.GetFromJsonAsync<JsonNode>("/api/admin/payments/prices");

        Assert.Equal(
            ApiEndpoints.StudentPlanDaysPerWeek,
            board!["tiers"]!.AsArray().Select(x => x!.GetValue<int>()).ToList());
        Assert.Contains("USD", board["currencies"]!.AsArray().Select(x => x!.GetValue<string>()));

        Func<int, string, bool> priced = (days, currency) => board["prices"]!.AsArray()
            .Any(x => x!["daysPerWeek"]!.GetValue<int>() == days
                && x["currency"]!.GetValue<string>() == currency);
        foreach (var days in ApiEndpoints.StudentPlanDaysPerWeek)
            Assert.True(priced(days, "USD"));

        Assert.Equal(HttpStatusCode.OK, (await SetPriceAsync(admin, 3, "GBP", 34.00m)).StatusCode);

        var after = await admin.GetFromJsonAsync<JsonNode>("/api/admin/payments/prices");
        Assert.Contains(after!["prices"]!.AsArray(), x =>
            x!["daysPerWeek"]!.GetValue<int>() == 3 && x["currency"]!.GetValue<string>() == "GBP");
    }

    [Fact]
    public async Task TheDeskPricesACurrencyAndThePlansPageShowsEveryTierInIt()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;

        foreach (var (days, gbp) in GbpPrices)
            Assert.Equal(HttpStatusCode.OK, (await SetPriceAsync(admin, days, "GBP", gbp)).StatusCode);

        var pricing = await Factory.Client.GetFromJsonAsync<JsonNode>("/api/pricing?currency=GBP");

        Assert.Equal("GBP", pricing!["currency"]!.GetValue<string>());
        Assert.Contains("GBP", pricing["currencies"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(pricing["cardCheckout"]!.GetValue<bool>());
        var plans = pricing["plans"]!.AsArray();
        Assert.Equal(GbpPrices.Length, plans.Count);
        foreach (var (days, gbp) in GbpPrices)
        {
            var plan = plans.Single(x => x!["daysPerWeek"]!.GetValue<int>() == days)!;
            Assert.Equal(gbp, plan["monthlyAmount"]!.GetValue<decimal>());
            Assert.Equal(PricingCatalog.ClassesPerMonth(days), plan["classesPerMonth"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TheFigureThePlansPageShowsIsTheFigureTheBillCarries()
    {
        // The page and the invoice read the same rows, so a student is never shown
        // one number and charged another.
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        foreach (var (days, gbp) in GbpPrices)
            await SetPriceAsync(admin, days, "GBP", gbp);

        var shown = (await Factory.Client.GetFromJsonAsync<JsonNode>("/api/pricing?country=GB"))!;
        Assert.Equal("GBP", shown!["currency"]!.GetValue<string>());
        var pageAmount = shown["plans"]!.AsArray()
            .Single(x => x!["daysPerWeek"]!.GetValue<int>() == 3)!["monthlyAmount"]!.GetValue<decimal>();

        var (student, user) = await AccountAsync(days: 3);
        using var __ = student;
        var invoice = await InvoiceOf(await IssueAsync(student, "GBP"));

        Assert.Equal(pageAmount, invoice["amount"]!.GetValue<decimal>());
        Assert.Equal(34.00m, invoice["amount"]!.GetValue<decimal>());
        Assert.Equal("GBP", invoice["currency"]!.GetValue<string>());

        var stored = await InvoiceAsync(InvoiceIdOf(invoice));
        Assert.Equal(3400, PricingCatalog.ToCents(stored!.Amount));
        Assert.Equal(user.Id, stored.StudentId);
    }

    [Fact]
    public async Task ThePlansPageQuotesTheCurrencyOfTheCountryBeingServed()
    {
        Assert.Equal("USD", (await PricingAsync(null))["currency"]!.GetValue<string>());
        Assert.Equal("USD", (await PricingAsync("US"))["currency"]!.GetValue<string>());
        // No GBP price rows in this host's ledger: pounds cannot be offered, so the
        // page falls back rather than showing a figure the server cannot bill.
        Assert.Equal("USD", (await PricingAsync("GB"))["currency"]!.GetValue<string>());
        Assert.Equal("USD", (await PricingAsync("EG"))["currency"]!.GetValue<string>());
    }

    [Fact]
    public async Task APriceChangeNeverRewritesAnInvoiceAlreadyOutOfTheDoor()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, user) = await AccountAsync(days: 2);
        using var __ = student;

        var before = await InvoiceOf(await IssueAsync(student, "USD"));
        var amountBefore = before["amount"]!.GetValue<decimal>();
        Assert.Equal(38.00m, amountBefore);

        var raised = await InvoiceOf(await SetPriceAsync(admin, 2, "USD", 47.50m));

        // The bill that is already out keeps its figure; only the next one moves.
        Assert.Equal(amountBefore, (await InvoiceAsync(InvoiceIdOf(before)))!.Amount);
        Assert.Equal(1, raised["openInvoicesUsingOldPrice"]!.GetValue<int>());

        var (next, _) = await AccountAsync(days: 2);
        using var ___ = next;
        var fresh = await InvoiceOf(await IssueAsync(next, "USD"));
        Assert.Equal(47.50m, fresh["amount"]!.GetValue<decimal>());
        Assert.Equal(2, await CountAsync(db => db.PaymentInvoices.Where(x => x.DaysPerWeek == 2)));
        Assert.Equal(user.Id, (await InvoiceAsync(InvoiceIdOf(before)))!.StudentId);
    }

    [Fact]
    public async Task AnImpossiblePriceIsRefused()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;

        var rejected = new[]
        {
            new { daysPerWeek = 9, currency = "USD", monthlyAmount = 20m },
            new { daysPerWeek = 0, currency = "USD", monthlyAmount = 20m },
            // A currency the account cannot charge in, however plausible the figure.
            new { daysPerWeek = 2, currency = "JPY", monthlyAmount = 20m },
            new { daysPerWeek = 2, currency = "EGP", monthlyAmount = 20m },
            new { daysPerWeek = 2, currency = "USD", monthlyAmount = 0m },
            new { daysPerWeek = 2, currency = "USD", monthlyAmount = -5m },
            new { daysPerWeek = 2, currency = "USD", monthlyAmount = 100001m },
            // A third decimal would be dropped at the provider, so it is refused here.
            new { daysPerWeek = 2, currency = "USD", monthlyAmount = 20.005m },
        };

        foreach (var price in rejected)
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.PutAsJsonAsync("/api/admin/payments/prices", price)).StatusCode);

        Assert.Equal(4, await CountAsync(db => db.PlanPrices.Where(x => x.Currency == "USD")));
    }

    [Fact]
    public async Task APaidInvoiceCannotBeVoidedAway()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, _, invoiceId) = await BilledAsync();
        using var __ = student;
        var ticket = await StartCheckoutAsync(student, invoiceId);
        await DeliverPaidAsync(ticket);

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/payments/invoices/{invoiceId}/void", new { reason = "trying to erase it" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("paid", (await InvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task ACancelledMonthCanBeBilledAgainAndItsCheckoutIsClosed()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, user, invoiceId) = await BilledAsync();
        using var __ = student;
        var ticket = await StartCheckoutAsync(student, invoiceId);

        // The desk must say why, because the ledger is the only account of it.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(
                $"/api/admin/payments/invoices/{invoiceId}/void", new { reason = " " })).StatusCode);

        var voided = await admin.PostAsJsonAsync(
            $"/api/admin/payments/invoices/{invoiceId}/void", new { reason = "wrong tier" });
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        Assert.Equal("voided", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal("voided", (await AttemptAsync(AttemptIdOf(ticket))).Status);
        Assert.Equal(1, await CountAsync(db => db.AuditLogs.Where(x => x.Action == "invoice.voided")));

        // A voided row is invisible to the unique index, so the corrected bill fits.
        var replacement = await InvoiceOf(await IssueAsync(student, "USD"));
        Assert.NotEqual(invoiceId, InvoiceIdOf(replacement));
        Assert.Equal(2, await CountAsync(db => db.PaymentInvoices.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task NothingUnverifiedCanBeRefunded()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var __ = student;

        // An open checkout has taken no money.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await RefundAsync(admin, AttemptIdOf(ticket), 1m)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await RefundAsync(admin, Guid.NewGuid(), 1m)).StatusCode);

        Assert.Empty(Provider.Refunds);
    }

    [Fact]
    public async Task ARefundCannotExceedWhatWasPaidAndAddsUpExactly()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var __ = student;
        await DeliverPaidAsync(ticket);
        var attemptId = AttemptIdOf(ticket);

        var half = await InvoiceOf(await RefundAsync(admin, attemptId, 20.00m));
        Assert.Equal(2000, half["refundedAmountCents"]!.GetValue<int>());
        Assert.Equal(2400, half["remainingCents"]!.GetValue<int>());
        Assert.Equal("captured", half["status"]!.GetValue<string>());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await RefundAsync(admin, attemptId, 24.01m)).StatusCode);

        var rest = await InvoiceOf(await RefundAsync(admin, attemptId, 24.00m));
        Assert.Equal(4400, rest["refundedAmountCents"]!.GetValue<int>());
        Assert.Equal(0, rest["remainingCents"]!.GetValue<int>());
        Assert.Equal("refunded", rest["status"]!.GetValue<string>());

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await RefundAsync(admin, attemptId, 1m)).StatusCode);

        // Two calls, two different idempotency keys, and the total is what was paid.
        Assert.Equal(2, Provider.Refunds.Count);
        Assert.Equal(new[] { 2000, 2400 }, Provider.Refunds.Select(r => r.AmountCents));
        Assert.Equal(2, Provider.Refunds.Select(r => r.Key).Distinct().Count());
        Assert.Equal(4400, Provider.Refunds.Sum(r => r.AmountCents));
        Assert.Equal("paid", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal(2, await CountAsync(db => db.AuditLogs.Where(x => x.Action == "payment.refunded")));
    }

    [Fact]
    public async Task TheProviderIsAskedWithAKeyTiedToTheRunningTotal()
    {
        // Two clicks that arrive together see the same refunded total, so they carry
        // the same key and the provider treats them as one call.
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, _, ticket) = await CheckoutAsync();
        using var __ = student;
        await DeliverPaidAsync(ticket);
        var attemptId = AttemptIdOf(ticket);

        await RefundAsync(admin, attemptId, 10m);
        await RefundAsync(admin, attemptId, 10m);

        Assert.Equal(2, Provider.Refunds.Count);
        Assert.Equal($"refund-{attemptId:N}-1000", Provider.Refunds[0].Key);
        Assert.Equal($"refund-{attemptId:N}-2000", Provider.Refunds[1].Key);
        Assert.Equal(2000, (await AttemptAsync(attemptId)).RefundedAmountCents);
    }

    [Fact]
    public async Task TheCallbackLogSaysWhatTheProviderClaimedAndWhatWeDidAboutIt()
    {
        var admin = await Factory.LoginAsync("admin");
        using var _ = admin;
        var (student, _, ticket) = await CheckoutAsync();
        using var __ = student;
        PayAtProvider(ticket);

        await SendPaidAsync(ticket, "evt_ledger_1");
        await SendPaidAsync(ticket, "evt_ledger_1");

        var rows = await admin.GetFromJsonAsync<JsonNode>("/api/admin/payments/callbacks");
        var row = rows!.AsArray().Single(x => x!["providerEventId"]!.GetValue<string>() == "evt_ledger_1")!;

        Assert.Equal("applied", row["outcome"]!.GetValue<string>());
        Assert.Equal("checkout.session.completed", row["eventType"]!.GetValue<string>());
        Assert.Equal(ReferenceOf(ticket), row["providerCheckoutId"]!.GetValue<string>());
        Assert.Equal(4400, row["amountCents"]!.GetValue<int>());
        Assert.Equal(1, await CountAsync(db => db.PaymentCallbackEvents.Where(x => x.ProviderEventId == "evt_ledger_1")));
    }

    // ---- helpers ----

    private static async Task<JsonNode> InvoiceOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static Task<HttpResponseMessage> SetPriceAsync(HttpClient admin, int days, string currency, decimal amount) =>
        admin.PutAsJsonAsync("/api/admin/payments/prices", new
        {
            daysPerWeek = days,
            currency,
            monthlyAmount = amount,
        });

    private static Task<HttpResponseMessage> RefundAsync(HttpClient admin, Guid attemptId, decimal amount) =>
        admin.PostAsJsonAsync($"/api/admin/payments/transactions/{attemptId}/refund", new { amount, reason = "desk refund" });

    private async Task<JsonNode> PricingAsync(string? country) =>
        (await Factory.Client.GetFromJsonAsync<JsonNode>(
            country is null ? "/api/pricing" : $"/api/pricing?country={country}"))!;
}
