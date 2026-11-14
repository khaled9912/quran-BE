using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Academy.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Academy.SecurityTests.Infrastructure;

/// <summary>
/// The payment tests all need the same thing: a student with a plan, a bill for
/// this month, and a checkout whose fate the test decides. It lives here so each
/// test reads as the question it asks rather than as setup.
///
/// The host talks to a provider the test drives by hand. Signature checking and
/// event decoding still run through the real Stripe code, because those are the
/// parts a payment is settled on.
/// </summary>
public abstract class PaymentHarness : ApiTestBase
{
    /// <summary>The USD monthly figure the boot seed prices three days a week at.</summary>
    protected const decimal UsdForThreeDays = 44.00m;

    protected const string WebhookPath = "/api/payments/stripe/webhook";

    protected PaymentHarness(PostgresFixture postgres) : base(postgres) =>
        Factory.TestServices = services => services.AddSingleton<IPaymentProvider>(Provider);

    protected FakePaymentProvider Provider { get; } = new();

    /// <summary>A signed-in student, optionally with a plan chosen and a bill issued.</summary>
    protected async Task<(HttpClient Student, AcademyUser User)> AccountAsync(int days, bool invoice = false)
    {
        var (student, user) = await Factory.LoginAsAsync("student");
        if (days > 0)
        {
            // A month is only billable once it has been taught, so a student with a
            // plan also gets a class in the month the academy bills.
            await TaughtAsync(user.Id);
            await Factory.WithDbAsync(async db =>
            {
                db.StudentOnboardings.Add(new StudentOnboarding { StudentId = user.Id, PlanDaysPerWeek = days });
                await db.SaveChangesAsync();
            });
        }

        if (invoice) Assert.Equal(HttpStatusCode.OK, (await IssueAsync(student)).StatusCode);
        return (student, user);
    }

    /// <summary>
    /// One class in the month the academy bills — the last one that has ended —
    /// which is what makes that month payable at all. Returns the teacher, so a
    /// test can put a price on the lesson it just made.
    /// </summary>
    protected async Task<Guid> TaughtAsync(Guid studentId, string status = "completed", int day = 5)
    {
        var teacher = await Factory.CreateUserAsync("teacher");
        var month = PaymentEndpoints.LastClosedMonth();
        var starts = new DateTimeOffset(month.Year, month.Month, day, 15, 0, 0, TimeSpan.Zero);
        await Factory.WithDbAsync(async db =>
        {
            db.Lessons.Add(new Lesson
            {
                TeacherId = teacher.Id,
                StudentId = studentId,
                StartsAt = starts,
                EndsAt = starts.AddMinutes(30),
                Status = status,
            });
            await db.SaveChangesAsync();
        });
        return teacher.Id;
    }

    /// <summary>A student with a three-day plan and this month's bill already out.</summary>
    protected async Task<(HttpClient Student, AcademyUser User, Guid InvoiceId)> BilledAsync()
    {
        var (student, user) = await AccountAsync(days: 3, invoice: true);
        return (student, user, InvoiceIdOf(await InvoiceViewAsync(student)));
    }

    /// <summary>A student with this month's bill out and a checkout open.</summary>
    protected async Task<(HttpClient Student, Guid InvoiceId, JsonNode Ticket)> CheckoutAsync()
    {
        var (student, _, invoiceId) = await BilledAsync();
        return (student, invoiceId, await StartCheckoutAsync(student, invoiceId));
    }

    protected static async Task<HttpResponseMessage> IssueAsync(HttpClient student, string? currency = "USD") =>
        await student.PostAsJsonAsync("/api/student/payments/invoice", new { currency });

    protected static async Task<JsonNode> InvoiceViewAsync(HttpClient student) =>
        (await student.GetFromJsonAsync<JsonNode>("/api/student/payments/invoice"))!;

    protected static Guid InvoiceIdOf(JsonNode view) => view["invoice"]!["id"]!.GetValue<Guid>();

    protected static string ReferenceOf(JsonNode ticket) => ticket["providerReference"]!.GetValue<string>();

    protected static Guid AttemptIdOf(JsonNode ticket) => ticket["transactionId"]!.GetValue<Guid>();

    protected static async Task<JsonNode> StartCheckoutAsync(HttpClient student, Guid invoiceId)
    {
        var response = await student.PostAsync($"/api/student/payments/invoice/{invoiceId}/checkout", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    protected static async Task<JsonNode> PollAsync(HttpClient student, Guid attemptId)
    {
        var response = await student.GetAsync($"/api/student/payments/attempt/{attemptId}/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    /// <summary>Pays the session a ticket opened, which is the only thing that can make a month paid.</summary>
    protected void PayAtProvider(JsonNode ticket, int? amountCents = null, string? currency = null) =>
        Provider.MarkPaid(ReferenceOf(ticket), amountCents, currency);

    /// <summary>Pushes the callback Stripe would send, signed with the endpoint's secret.</summary>
    protected async Task<HttpResponseMessage> SendEventAsync(
        string body,
        string? signature = null,
        DateTimeOffset? signedAt = null)
    {
        using var client = Factory.RawClient;
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        signature ??= FakePaymentProvider.SignedWith(body, signedAt ?? DateTimeOffset.UtcNow);
        if (signature is not null)
            request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return await client.SendAsync(request);
    }

    /// <summary>The event that says a ticket's session was paid, posted as Stripe posts it.</summary>
    protected async Task<HttpResponseMessage> SendPaidAsync(
        JsonNode ticket,
        string? eventId = null,
        bool liveMode = false,
        string type = "checkout.session.completed")
    {
        var body = FakePaymentProvider.CompletedEvent(
            eventId ?? $"evt_{Guid.NewGuid():N}",
            ReferenceOf(ticket),
            $"t{AttemptIdOf(ticket):N}",
            liveMode,
            type);
        return await SendEventAsync(body);
    }

    /// <summary>Paid at the provider and pushed the callback, which is how a month really settles.</summary>
    protected async Task DeliverPaidAsync(JsonNode ticket, string? eventId = null)
    {
        PayAtProvider(ticket);
        Assert.Equal(HttpStatusCode.OK, (await SendPaidAsync(ticket, eventId)).StatusCode);
    }

    /// <summary>What the academy believes happened to one callback, straight off the ledger.</summary>
    protected Task<PaymentCallbackEvent?> CallbackAsync(string eventId) =>
        Factory.WithDbAsync(db => db.PaymentCallbackEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderEventId == eventId));

    protected Task<PaymentInvoice?> InvoiceAsync(Guid id) =>
        Factory.WithDbAsync(db => db.PaymentInvoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));

    protected Task<PaymentTransaction> AttemptAsync(Guid id) =>
        Factory.WithDbAsync(db => db.PaymentTransactions.AsNoTracking().FirstAsync(x => x.Id == id));

    protected Task<int> CountAsync<T>(Func<AcademyDbContext, IQueryable<T>> query) =>
        Factory.WithDbAsync(db => query(db).CountAsync());

    protected Task<List<T>> RowsAsync<T>(Func<AcademyDbContext, IQueryable<T>> query) =>
        Factory.WithDbAsync(db => query(db).ToListAsync());
}
