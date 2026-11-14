using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The money path as a student walks it: bill me, open a checkout, pay. Every
/// test here asks the same question a different way — can a caller make the
/// academy believe a month is paid when the provider has not said so?
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PaymentCheckoutTests(PostgresFixture postgres) : PaymentHarness(postgres)
{
    [Fact]
    public async Task OnlyASignedInStudentCanReachTheirOwnBills()
    {
        var (student, _, _) = await BilledAsync();
        using var _ = student;

        foreach (var (label, client) in new[]
        {
            ("anonymous", Factory.Client),
            ("teacher", await Factory.LoginAsync("teacher")),
            ("support", await Factory.LoginAsync("support")),
        })
        {
            using (client)
            {
                var status = (await client.GetAsync("/api/student/payments/invoice")).StatusCode;
                Assert.True(
                    status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{label} got {status}");
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/student/payments/invoice")).StatusCode);
    }

    [Fact]
    public async Task TheInvoiceIsPricedFromTheTableWhateverTheRequestSays()
    {
        var (student, user) = await AccountAsync(days: 3);
        using var _ = student;

        // A caller may name a currency. Not a figure, a tier, a month, or a status.
        var response = await student.PostAsJsonAsync("/api/student/payments/invoice", new
        {
            currency = "usd",
            amount = 0.01m,
            daysPerWeek = 5,
            month = "2020-01",
            status = "paid",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<JsonNode>();
        var invoice = view!["invoice"]!;

        Assert.Equal("USD", view["currency"]!.GetValue<string>());
        Assert.Equal(UsdForThreeDays, view["amount"]!.GetValue<decimal>());
        Assert.Equal(3, view["daysPerWeek"]!.GetValue<int>());
        Assert.Equal("open", invoice["status"]!.GetValue<string>());
        Assert.True(invoice["paidAt"] is null);
        // The month is the last one that has ended in the academy's clock, and
        // "2020-01" in the body changed nothing.
        Assert.Equal(
            PaymentEndpoints.MonthKey(PaymentEndpoints.LastClosedMonth()),
            view["month"]!.GetValue<string>());

        var stored = await InvoiceAsync(InvoiceIdOf(view));
        Assert.Equal(UsdForThreeDays, stored!.Amount);
        Assert.Equal(user.Id, stored.StudentId);
        Assert.Null(stored.PaidAt);
    }

    [Fact]
    public async Task ThePreviewAndTheBillQuoteTheSameCurrencyAndFigure()
    {
        var (student, _) = await AccountAsync(days: 3);
        using var _ = student;

        // A currency the academy has not priced falls back to the one it has,
        // both on the preview and on the bill, so no page can show a figure of
        // nothing and no invoice can be issued for one.
        var preview = await student.GetFromJsonAsync<JsonNode>(
            "/api/student/payments/invoice?currency=EUR");

        Assert.Equal("USD", preview!["currency"]!.GetValue<string>());
        Assert.Equal(UsdForThreeDays, preview["amount"]!.GetValue<decimal>());
        Assert.True(preview["invoice"] is null);

        var billed = await (await IssueAsync(student, "EUR")).Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal(preview["currency"]!.GetValue<string>(), billed!["currency"]!.GetValue<string>());
        Assert.Equal(preview["amount"]!.GetValue<decimal>(), billed["amount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task OneMonthIsBilledOnceHoweverOftenItIsAskedFor()
    {
        var (student, user) = await AccountAsync(days: 3, invoice: true);
        using var _ = student;

        var first = await InvoiceViewAsync(student);
        var second = await IssueAsync(student);
        var again = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(InvoiceIdOf(first), InvoiceIdOf(again));
        Assert.Equal(1, await CountAsync(db => db.PaymentInvoices.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task AStudentWithoutAChosenPlanCannotBeBilledYet()
    {
        var (student, user) = await AccountAsync(days: 0);
        using var _ = student;

        Assert.Equal(HttpStatusCode.Conflict, (await IssueAsync(student)).StatusCode);
        Assert.Equal(0, await CountAsync(db => db.PaymentInvoices.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task TheMonthBilledIsTheLastOneThatHasEnded()
    {
        var (student, _) = await AccountAsync(days: 3);
        using var _ = student;

        var month = PaymentEndpoints.MonthKey(PaymentEndpoints.LastClosedMonth());

        var preview = await InvoiceViewAsync(student);
        Assert.Equal(month, preview["month"]!.GetValue<string>());
        Assert.Equal(UsdForThreeDays, preview["amount"]!.GetValue<decimal>());

        var billed = await (await IssueAsync(student)).Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(month, billed!["month"]!.GetValue<string>());
        Assert.Equal(month, billed["invoice"]!["month"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStudentIsNeverBilledForAMonthThatIsStillHappening()
    {
        var (student, user) = await AccountAsync(days: 3);
        using var _ = student;

        // The plans page promises payment after the month, so the ongoing month is
        // not a thing a student can be charged for — not by preview, not by bill.
        var ongoing = PaymentEndpoints.MonthKey(PaymentEndpoints.CurrentMonth());

        var preview = await student.GetAsync($"/api/student/payments/invoice?month={ongoing}");
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);

        var asked = await student.PostAsJsonAsync("/api/student/payments/invoice", new { month = ongoing });
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var view = await asked.Content.ReadFromJsonAsync<JsonNode>();

        Assert.NotEqual(ongoing, view!["month"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync(db => db.PaymentInvoices.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task AMonthWithNoClassesHeldIsNotBilled()
    {
        var (student, user) = await AccountAsync(days: 0);
        using var _ = student;
        // The plan is chosen, but every class that month was cancelled.
        await Factory.WithDbAsync(async db =>
        {
            db.StudentOnboardings.Add(new StudentOnboarding { StudentId = user.Id, PlanDaysPerWeek = 3 });
            await db.SaveChangesAsync();
        });
        await TaughtAsync(user.Id, status: "cancelled");

        Assert.Equal(0m, (await InvoiceViewAsync(student))["amount"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.Conflict, (await IssueAsync(student)).StatusCode);
        Assert.Equal(0, await CountAsync(db => db.PaymentInvoices.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task ACardPaidMonthCannotBeClaimedAsATransferAsWell()
    {
        var (student, user) = await AccountAsync(days: 3, invoice: true);
        using var _ = student;
        var month = PaymentEndpoints.MonthKey(PaymentEndpoints.LastClosedMonth());
        var teacherId = await Factory.WithDbAsync(db => db.Lessons
            .Where(x => x.StudentId == user.Id)
            .Select(x => x.TeacherId)
            .FirstAsync());

        // A priced lesson is what the transfer rail would have charged for, so the
        // claim below is one the student could really have made.
        var admin = await Factory.LoginAsync("admin");
        using var __ = admin;
        await admin.PutAsJsonAsync(
            $"/api/admin/billing/rates/{teacherId}", new { studentPriceUsd = 12.50m, payoutEgp = 300m });

        var ticket = await StartCheckoutAsync(student, InvoiceIdOf(await InvoiceViewAsync(student)));
        await DeliverPaidAsync(ticket);

        var statement = await student.GetFromJsonAsync<JsonNode>($"/api/student/billing?month={month}");
        Assert.True(statement!["cardPaid"]!.GetValue<bool>());
        Assert.False(statement["canClaim"]!.GetValue<bool>());

        var claim = await student.PostAsJsonAsync("/api/student/billing/claims", new { month });
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        Assert.Equal(0, await CountAsync(db => db.MonthlyPaymentClaims.Where(x => x.StudentId == user.Id)));
    }

    [Fact]
    public async Task ATransferUnderReviewCannotOpenACardCheckoutAsWell()
    {
        var (student, user) = await AccountAsync(days: 3, invoice: true);
        using var _ = student;
        var invoiceId = InvoiceIdOf(await InvoiceViewAsync(student));
        var month = PaymentEndpoints.MonthKey(PaymentEndpoints.LastClosedMonth());
        var teacherId = await Factory.WithDbAsync(db => db.Lessons
            .Where(x => x.StudentId == user.Id)
            .Select(x => x.TeacherId)
            .FirstAsync());

        var admin = await Factory.LoginAsync("admin");
        using var __ = admin;
        await admin.PutAsJsonAsync(
            $"/api/admin/billing/rates/{teacherId}", new { studentPriceUsd = 12.50m, payoutEgp = 300m });

        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync(
            "/api/student/billing/claims", new { month })).StatusCode);

        // Both rails are the same money. Opening a card session now would collect it
        // twice, once by transfer and once by card.
        var response = await student.PostAsync($"/api/student/payments/invoice/{invoiceId}/checkout", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountAsync(db => db.PaymentTransactions.Where(x => x.InvoiceId == invoiceId)));
    }

    [Fact]
    public async Task ACurrencyTheAcademyHasNotPricedIsNeverBilled()
    {
        var (student, _) = await AccountAsync(days: 3);
        using var _ = student;

        // GBP is enabled on the account but carries no price rows. The request is
        // answered in the currency the academy can actually honour, and the invoice
        // it produces is not a half-priced guess at anything.
        var response = await IssueAsync(student, "GBP");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<JsonNode>();

        Assert.Equal("USD", view!["currency"]!.GetValue<string>());
        Assert.Equal(UsdForThreeDays, view["amount"]!.GetValue<decimal>());

        var stored = await InvoiceAsync(InvoiceIdOf(view));
        Assert.Equal("USD", stored!.Currency);
        Assert.Equal(4400, PricingCatalog.ToCents(stored.Amount));
    }

    [Fact]
    public async Task AnotherStudentsInvoiceIsNotFoundRatherThanForbidden()
    {
        var mine = await BilledAsync();
        var theirs = await BilledAsync();
        using var _ = mine.Student;
        using var __ = theirs.Student;

        var response = await mine.Student.PostAsync(
            $"/api/student/payments/invoice/{theirs.InvoiceId}/checkout", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAsync(db => db.PaymentTransactions.Where(x => x.InvoiceId == theirs.InvoiceId)));
        Assert.Equal("open", (await InvoiceAsync(theirs.InvoiceId))!.Status);
    }

    [Fact]
    public async Task AnOpenCheckoutLeavesTheMonthUnpaid()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        var attemptId = AttemptIdOf(ticket);

        Assert.False(string.IsNullOrWhiteSpace(ticket["clientSecret"]!.GetValue<string>()));
        Assert.Equal("pk_test_local", ticket["publishableKey"]!.GetValue<string>());
        Assert.Equal("USD", ticket["currency"]!.GetValue<string>());
        Assert.Equal(UsdForThreeDays, ticket["amount"]!.GetValue<decimal>());

        // What the academy asked the provider to charge, taken from the invoice
        // rather than from anything the browser sent.
        var request = Assert.Single(Provider.Requests, x => x.MerchantReference == $"t{attemptId:N}");
        Assert.Equal(4400, request.AmountCents);
        Assert.Equal("USD", request.Currency);

        var status = await PollAsync(student, attemptId);
        Assert.False(status["paid"]!.GetValue<bool>());
        Assert.Equal("open", status["providerStatus"]!.GetValue<string>());

        var invoice = await InvoiceAsync(invoiceId);
        Assert.Equal("awaiting_payment", invoice!.Status);
        Assert.Null(invoice.PaidAt);
    }

    [Fact]
    public async Task AskingForACheckoutTwiceReusesTheOneStillOpen()
    {
        var (student, invoiceId, first) = await CheckoutAsync();
        using var _ = student;

        var second = await StartCheckoutAsync(student, invoiceId);

        Assert.Equal(AttemptIdOf(first), AttemptIdOf(second));
        Assert.Equal(first["clientSecret"]!.GetValue<string>(), second["clientSecret"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync(db => db.PaymentTransactions.Where(x => x.InvoiceId == invoiceId)));
        // Two open sessions for one month is how a student pays twice. One was opened.
        Assert.Single(Provider.Requests, x => x.MerchantReference == $"t{AttemptIdOf(first):N}");
    }

    [Fact]
    public async Task ACheckoutThatExpiredOpensAFreshAttempt()
    {
        var (student, invoiceId, first) = await CheckoutAsync();
        using var _ = student;
        Provider.MarkExpired(ReferenceOf(first));

        var second = await StartCheckoutAsync(student, invoiceId);

        Assert.NotEqual(AttemptIdOf(first), AttemptIdOf(second));
        var statuses = await Factory.WithDbAsync(db => db.PaymentTransactions
            .AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .ToDictionaryAsync(x => x.Id, x => x.Status));
        Assert.Equal(2, statuses.Count);
        Assert.Equal("voided", statuses[AttemptIdOf(first)]);
        Assert.Equal("pending", statuses[AttemptIdOf(second)]);
    }

    [Fact]
    public async Task PollingTheAttemptIsWhatRevealsAPayment()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        var attemptId = AttemptIdOf(ticket);
        PayAtProvider(ticket);

        var status = await PollAsync(student, attemptId);

        Assert.True(status["paid"]!.GetValue<bool>());
        Assert.Equal("complete", status["providerStatus"]!.GetValue<string>());
        Assert.True(status["providerReached"]!.GetValue<bool>());

        var invoice = await InvoiceAsync(invoiceId);
        Assert.Equal("paid", invoice!.Status);
        Assert.NotNull(invoice.PaidAt);

        var attempt = await AttemptAsync(attemptId);
        Assert.Equal("captured", attempt.Status);
        Assert.NotNull(attempt.ConfirmedAt);
        Assert.Equal($"pi_{ReferenceOf(ticket)}", attempt.ProviderTransactionId);
        Assert.Equal("visa", attempt.PaymentBrand);
        Assert.Equal(1, await CountAsync(db => db.Notifications.Where(x => x.DedupeKey == $"payment:{invoiceId}")));
        Assert.Equal(1, await CountAsync(db => db.AuditLogs.Where(x => x.Action == "payment.captured")));
    }

    [Fact]
    public async Task PollingAnotherStudentsAttemptIsNotFound()
    {
        var (mine, _, _) = await BilledAsync();
        var (theirs, invoiceId, ticket) = await CheckoutAsync();
        using var _ = mine;
        using var __ = theirs;
        PayAtProvider(ticket);

        var response = await mine.GetAsync(
            $"/api/student/payments/attempt/{AttemptIdOf(ticket)}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task ARedirectIsNotAReceipt()
    {
        // The checkout was opened and the browser is back. Nothing a student can
        // post or navigate to settles the month on its own.
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        var attemptId = AttemptIdOf(ticket);

        foreach (var path in new[]
        {
            $"/api/student/payments/attempt/{attemptId}/confirm",
            $"/api/student/payments/attempt/{attemptId}/success",
            $"/api/student/payments/invoice/{invoiceId}/paid",
            $"/api/student/payments/invoice/{invoiceId}/status",
        })
        {
            var get = (await student.GetAsync(path)).StatusCode;
            var post = (await student.PostAsync(path, null)).StatusCode;
            Assert.True(
                get is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                    && post is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{path}: GET {get} POST {post}");
        }

        Assert.Equal("awaiting_payment", (await InvoiceAsync(invoiceId))!.Status);
        Assert.Equal(0, await CountAsync(db => db.PaymentCallbackEvents));
    }

    [Fact]
    public async Task APaidMonthCannotBeCheckedOutAgain()
    {
        var (student, invoiceId, ticket) = await CheckoutAsync();
        using var _ = student;
        PayAtProvider(ticket);
        await PollAsync(student, AttemptIdOf(ticket));

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await student.PostAsync($"/api/student/payments/invoice/{invoiceId}/checkout", null)).StatusCode);
        Assert.Equal("paid", (await InvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task ACheckoutTheProviderRefusesIsRecordedAndChangesNothing()
    {
        var (student, _, invoiceId) = await BilledAsync();
        using var _ = student;
        Provider.AcceptsCheckout = false;

        var response = await student.PostAsync($"/api/student/payments/invoice/{invoiceId}/checkout", null);

        Provider.AcceptsCheckout = true;
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var attempt = await Factory.WithDbAsync(db => db.PaymentTransactions
            .AsNoTracking()
            .FirstAsync(x => x.InvoiceId == invoiceId));
        Assert.Equal("failed", attempt.Status);
        Assert.Null(attempt.ProviderCheckoutId);
        Assert.Equal("open", (await InvoiceAsync(invoiceId))!.Status);
    }
}
