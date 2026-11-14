using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Academy.Api;
using Academy.SecurityTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// A host with no card credentials, which is what this environment is until the
/// academy's keys are put in it. Nothing here may take money, fake a receipt, or
/// hide the prices the public page quotes.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class PaymentWithoutStripeTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task NoCheckoutOpensAndNoCallbackSettlesAnything()
    {
        var (student, user) = await Factory.LoginAsAsync("student");
        using var _ = student;
        var teacher = await Factory.CreateUserAsync("teacher");
        var taught = new DateTimeOffset(
            PaymentEndpoints.LastClosedMonth().Year,
            PaymentEndpoints.LastClosedMonth().Month,
            5,
            15,
            0,
            0,
            TimeSpan.Zero);
        await Factory.WithDbAsync(async db =>
        {
            db.StudentOnboardings.Add(new StudentOnboarding { StudentId = user.Id, PlanDaysPerWeek = 3 });
            // The month is only billable once it has been taught.
            db.Lessons.Add(new Lesson
            {
                TeacherId = teacher.Id,
                StudentId = user.Id,
                StartsAt = taught,
                EndsAt = taught.AddMinutes(30),
                Status = "completed",
            });
            await db.SaveChangesAsync();
        });

        var issued = await student.PostAsJsonAsync("/api/student/payments/invoice", new { currency = "USD" });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var invoiceId = JsonNode.Parse(await issued.Content.ReadAsStringAsync())!["invoice"]!["id"]!.GetValue<Guid>();

        var response = await student.PostAsync(
            $"/api/student/payments/invoice/{invoiceId}/checkout", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // A callback is worthless without the secret that signs it, and this host
        // has none: it cannot reach the money and cannot even leave a record.
        var body = FakePaymentProvider.CompletedEvent("evt_unconfigured", "cs_any", null);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostEventAsync(body, FakePaymentProvider.SignedWith(body, DateTimeOffset.UtcNow))).StatusCode);

        Assert.Equal(0, await Factory.WithDbAsync(db => db.PaymentCallbackEvents.CountAsync()));
        Assert.Equal(
            "open",
            await Factory.WithDbAsync(db => db.PaymentInvoices
                .Where(x => x.StudentId == user.Id)
                .Select(x => x.Status)
                .FirstAsync()));
    }

    [Fact]
    public async Task ThePricesAreStillPublishedWithoutAProvider()
    {
        // The plans page is not gated on credentials: a visitor sees the same
        // figures a student with a working checkout is later billed.
        using var client = Factory.Client;
        var pricing = await client.GetFromJsonAsync<JsonNode>("/api/pricing");

        Assert.Equal("USD", pricing!["currency"]!.GetValue<string>());
        Assert.Equal(4, pricing["plans"]!.AsArray().Count);
        Assert.True(await Factory.WithDbAsync(db => db.PlanPrices.AnyAsync(x => x.Currency == "USD")));

        // The public answer a page needs before it offers a Pay Now button.
        Assert.False(pricing["cardCheckout"]!.GetValue<bool>());
    }

    private async Task<HttpResponseMessage> PostEventAsync(string body, string signature)
    {
        using var client = Factory.RawClient;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/stripe/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return await client.SendAsync(request);
    }
}
