using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed record PublicPlanDto(int DaysPerWeek, int ClassesPerMonth, decimal MonthlyAmount);

/// <summary>
/// The price list a visitor sees. <see cref="Currency"/> is the currency an
/// invoice would later be issued in for the same visitor, and
/// <see cref="CardCheckout"/> says whether a card can actually be taken in it,
/// so no page advertises a Pay Now button this host cannot honour.
///
/// The three hour/minute figures are the same live rule numbers the signed
/// agreements are filled from, so a public answer about cancelling cannot
/// drift from what the academy actually enforces.
/// </summary>
public sealed record PricingDto(
    string Currency,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<PublicPlanDto> Plans,
    bool CardCheckout,
    int FreeCancelHours,
    int TeacherNoticeHours,
    int NoShowGraceMinutes);

public sealed record PaymentAttemptDto(
    Guid Id,
    string Provider,
    string Status,
    int AmountCents,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    int? PresentmentAmountCents,
    string? PresentmentCurrency,
    string? FailureReason);

public sealed record InvoiceDto(
    Guid Id,
    string Month,
    int DaysPerWeek,
    string Currency,
    decimal Amount,
    string Status,
    string? Note,
    DateTimeOffset IssuedAt,
    DateTimeOffset? PaidAt,
    IReadOnlyList<PaymentAttemptDto> Attempts);

/// <summary>
/// The month as the student's screen needs it: what their tier costs whether or
/// not a bill has been issued for it yet.
/// </summary>
public sealed record InvoiceViewDto(
    string Month,
    int DaysPerWeek,
    string Currency,
    decimal Amount,
    InvoiceDto? Invoice);

public sealed record CheckoutTicketDto(
    Guid InvoiceId,
    Guid TransactionId,
    string ClientSecret,
    string PublishableKey,
    string ProviderReference,
    string Currency,
    decimal Amount);

public sealed record PaymentStatusDto(
    Guid InvoiceId,
    string InvoiceStatus,
    bool Paid,
    string ProviderStatus,
    bool ProviderReached);

public sealed record IssueInvoiceRequest(string? Currency);

/// <summary>
/// Card checkout: the public price list, a student's invoice, the embedded
/// checkout, and the callback that settles it.
///
/// The rule running through this file is that nothing a browser says can mark an
/// invoice paid. A payment is confirmed by reading the session back from the
/// provider's own API and comparing it with what the academy asked for.
/// </summary>
public static class CheckoutEndpoints
{
    private static ILogger logger = null!;

    public static void MapCheckout(this WebApplication app)
    {
        // Endpoint handlers have no instance to hold a logger, so the
        // application logger is taken once, when the routes are mapped.
        logger = app.Logger;

        var api = app.MapGroup("/api");

        // The plans page is public, and it reads the same rows an invoice is
        // priced from.
        api.MapGet("/pricing", PricingAsync).AllowAnonymous();

        var student = api.MapGroup("/student/payments").RequireAuthorization("StudentOnly");
        student.MapGet("/invoice", CurrentInvoiceAsync);
        student.MapPost("/invoice", IssueInvoiceAsync).RequireRateLimiting("invoice");
        student.MapPost("/invoice/{id:guid}/checkout", StartCheckoutAsync).RequireRateLimiting("payment-initiate");
        student.MapGet("/attempt/{id:guid}/status", PollAttemptAsync).RequireRateLimiting("payment-status");

        // Stripe posts here with no session and no Origin header, so this is the
        // one mutation both the origin check and cookie auth let through. It stays
        // partitioned by address: an unverifiable signature costs one hmac, and
        // even that must not be free at volume.
        app.MapPost("/api/payments/stripe/webhook", WebhookAsync)
            .AllowAnonymous()
            .RequireRateLimiting("stripe-webhook");
    }

    /// <summary>
    /// The price list a visitor sees. Its <see cref="PricingDto.Currency"/> is the
    /// currency an invoice would later be issued in for the same visitor, so the
    /// figure on the page and the figure on the bill come from one lookup.
    /// </summary>
    private static async Task<IResult> PricingAsync(
        string? currency,
        string? country,
        AcademyDbContext db,
        StripeOptions options,
        IPaymentProvider provider,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var catalog = await PricingCatalog.LoadAsync(db, options, DefaultCurrency(configuration), cancellationToken);
        var rules = await LessonPolicyService.GetPolicyAsync(db, cancellationToken);
        var resolved = catalog.ResolveCurrency(currency, country);
        if (resolved.Length == 0)
            return Results.Ok(new PricingDto("", [], [], false, rules.StudentFreeCancelHours, rules.TeacherFreeCancelHours, rules.NoShowGraceMinutes));

        var plans = ApiEndpoints.StudentPlanDaysPerWeek
            .Select(d => (Days: d, Amount: catalog.PriceOrZero(resolved, d)))
            .Where(x => x.Amount > 0m)
            .Select(x => new PublicPlanDto(x.Days, PricingCatalog.ClassesPerMonth(x.Days), x.Amount))
            .ToList();

        return Results.Ok(new PricingDto(
            resolved,
            catalog.CompleteCurrencies(),
            plans,
            provider.IsConfigured && provider.SupportsCurrency(resolved),
            rules.StudentFreeCancelHours,
            rules.TeacherFreeCancelHours,
            rules.NoShowGraceMinutes));
    }

    /// <summary>
    /// The month as the student's screen needs it: what their plan costs whether or
    /// not a bill has been issued for it yet.
    ///
    /// <paramref name="currency"/> is the one the page is already showing them. It
    /// selects which of the academy's own price rows to quote, exactly as issuing
    /// the bill does, so a figure cannot change underneath a student between
    /// preview and invoice. A bill that already exists keeps its own currency.
    ///
    /// The month is the last one that has ended, and the answer is zero for a month
    /// with no classes in it: a plan is bought for the teaching that took place, so
    /// a bill exists only once a month has something to pay for.
    /// </summary>
    private static async Task<IResult> CurrentInvoiceAsync(
        string? month,
        string? currency,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        StripeOptions options,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var studentId = ApiEndpoints.UserId(principal);
        var billable = PaymentEndpoints.LastClosedMonth();
        var target = PaymentEndpoints.TryParseMonth(month, out var parsed) ? parsed : billable;
        if (target > billable)
            return Results.BadRequest(new { message = "That month has not ended yet." });

        var key = PaymentEndpoints.MonthKey(target);
        var invoice = await db.PaymentInvoices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.Month == key && x.Status != "voided", cancellationToken);

        if (invoice is not null)
            return Results.Ok(ToView(invoice, await AttemptsAsync(db, [invoice.Id], cancellationToken)));

        var planDays = await PlannedDaysAsync(db, studentId, cancellationToken);
        var catalog = await PricingCatalog.LoadAsync(db, options, DefaultCurrency(configuration), cancellationToken);
        var resolved = catalog.ResolveCurrency(currency, null);
        var priced = planDays > 0 && resolved.Length > 0 && await HasClassesAsync(db, studentId, target, cancellationToken);

        return Results.Ok(new InvoiceViewDto(
            key,
            planDays,
            resolved,
            priced ? catalog.PriceOrZero(resolved, planDays) : 0m,
            null));
    }

    /// <summary>
    /// Issues the month's bill. The amount comes from the price table and the tier
    /// on the student's own record; the request carries a currency at most, never a
    /// figure, so there is nothing here for a caller to negotiate with.
    /// </summary>
    private static async Task<IResult> IssueInvoiceAsync(
        IssueInvoiceRequest? request,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        StripeOptions options,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var studentId = ApiEndpoints.UserId(principal);
        var target = PaymentEndpoints.LastClosedMonth();
        var key = PaymentEndpoints.MonthKey(target);

        var existing = await db.PaymentInvoices
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.Month == key && x.Status != "voided", cancellationToken);

        if (existing is not null)
            return Results.Ok(ToView(existing, await AttemptsAsync(db, [existing.Id], cancellationToken)));

        var planDays = await PlannedDaysAsync(db, studentId, cancellationToken);
        if (planDays == 0)
            return Results.Conflict(new { message = "Choose a plan before requesting an invoice." });

        if (await HasClassesAsync(db, studentId, target, cancellationToken) is false)
            return Results.Conflict(new { message = "No classes took place in that month, so there is nothing to pay." });

        var catalog = await PricingCatalog.LoadAsync(db, options, DefaultCurrency(configuration), cancellationToken);
        var currency = catalog.ResolveCurrency(request?.Currency, null);

        if (currency.Length == 0 || !catalog.TryGetPrice(currency, planDays, out var amount))
            return Results.Conflict(new { message = "That currency is not priced for your plan yet." });

        var invoice = new PaymentInvoice
        {
            StudentId = studentId,
            Month = key,
            DaysPerWeek = planDays,
            Currency = currency,
            Amount = amount,
            Status = "open",
            Note = $"{planDays} days a week · {PricingCatalog.ClassesPerMonth(planDays)} classes",
        };

        db.PaymentInvoices.Add(invoice);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request priced the same month while this one worked; the
            // invoice that won is the answer.
            var raced = await db.PaymentInvoices
                .FirstOrDefaultAsync(x => x.StudentId == studentId && x.Month == key && x.Status != "voided", cancellationToken);

            return raced is null
                ? Results.Conflict(new { message = "This invoice could not be issued. Please try again." })
                : Results.Ok(ToView(raced, await AttemptsAsync(db, [raced.Id], cancellationToken)));
        }

        return Results.Ok(ToView(invoice, []));
    }

    /// <summary>
    /// Opens a checkout for an invoice the caller owns.
    ///
    /// An attempt that is still live is reused rather than replaced: two open
    /// checkouts for one month is how a student pays the same bill twice.
    /// </summary>
    private static async Task<IResult> StartCheckoutAsync(
        Guid id,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        IPaymentProvider provider,
        RealtimeNotifier realtime,
        CancellationToken cancellationToken)
    {
        var studentId = ApiEndpoints.UserId(principal);
        var invoice = await db.PaymentInvoices
            .Include(x => x.Student)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        // Shaped exactly like "not found": another student's invoice is not a thing
        // this caller may learn exists, let alone pay.
        if (invoice is null || invoice.StudentId != studentId) return Results.NotFound();

        if (invoice.Status == "paid") return Results.Conflict(new { message = "This month is already paid." });
        if (invoice.Status == "voided") return Results.Conflict(new { message = "This invoice is no longer payable." });

        // A transfer the academy is reviewing for the same month is that month's
        // money arriving another way. Two rails settling one bill is a double
        // charge, and neither side can see the other until money has moved.
        var claimed = await db.MonthlyPaymentClaims
            .AsNoTracking()
            .AnyAsync(
                x => x.StudentId == studentId && x.Month == invoice.Month && x.Status != "rejected",
                cancellationToken);
        if (claimed)
            return Results.Conflict(new { message = "A bank transfer for this month is already with the academy." });

        var amountCents = PricingCatalog.ToCents(invoice.Amount);
        if (amountCents <= 0) return Results.Conflict(new { message = "This invoice has nothing to pay." });

        var live = await db.PaymentTransactions
            .FirstOrDefaultAsync(x => x.InvoiceId == invoice.Id && x.Status == "pending" && x.ProviderCheckoutId != null, cancellationToken);

        if (live is not null)
        {
            var state = await provider.QueryCheckoutAsync(live.ProviderCheckoutId!, cancellationToken);
            if (!state.Found)
                return Results.Problem("The payment provider is unavailable right now.", statusCode: StatusCodes.Status502BadGateway);

            if (state.Paid || state.Status == "expired")
            {
                await SettleAsync(db, live, state, null, realtime, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await db.Entry(live).ReloadAsync(cancellationToken);
                await db.Entry(invoice).ReloadAsync(cancellationToken);

                if (invoice.Status == "paid") return Results.Conflict(new { message = "This month is already paid." });
            }
            else if (state.Status == "open" && !string.IsNullOrWhiteSpace(state.ClientSecret))
            {
                return Results.Ok(new CheckoutTicketDto(
                    invoice.Id, live.Id, state.ClientSecret!, provider.PublicKey ?? "", live.ProviderCheckoutId!,
                    live.Currency, invoice.Amount));
            }
        }

        var attempt = new PaymentTransaction
        {
            InvoiceId = invoice.Id,
            Provider = provider.Name,
            AmountCents = amountCents,
            Currency = invoice.Currency,
            Status = "pending",
        };
        // The reference is the row's own id, so a retry of this call cannot be
        // mistaken for a second one, and a callback can only ever name one attempt.
        attempt.MerchantReference = $"t{attempt.Id:N}";

        db.PaymentTransactions.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        var ticket = await provider.CreateCheckoutAsync(
            new CheckoutRequest(
                amountCents,
                invoice.Currency,
                attempt.MerchantReference,
                DescriptionFor(invoice),
                new CheckoutPayer(invoice.Student?.FullName, invoice.Student?.Email, null)),
            cancellationToken);

        if (!ticket.Accepted)
        {
            attempt.Status = "failed";
            attempt.FailureReason = Clipped(ticket.Error, 300);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Checkout could not be opened for invoice {Invoice}.", invoice.Id);
            return Results.Problem(
                "The payment provider could not open a checkout. Please try again.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        attempt.ProviderCheckoutId = ticket.ProviderReference;
        invoice.Status = "awaiting_payment";
        invoice.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new CheckoutTicketDto(
            invoice.Id, attempt.Id, ticket.ClientSecret ?? "", ticket.PublicKey ?? "",
            ticket.ProviderReference, invoice.Currency, invoice.Amount));
    }

    /// <summary>
    /// The answer a student's screen polls. It re-reads the provider rather than
    /// waiting on a callback, and it is the only way a browser learns that a
    /// payment went through.
    /// </summary>
    private static async Task<IResult> PollAttemptAsync(
        Guid id,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        IPaymentProvider provider,
        RealtimeNotifier realtime,
        CancellationToken cancellationToken)
    {
        var studentId = ApiEndpoints.UserId(principal);
        var attempt = await db.PaymentTransactions
            .Include(x => x.Invoice)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (attempt?.Invoice is null || attempt.Invoice.StudentId != studentId) return Results.NotFound();

        var providerStatus = "unknown";
        var reached = false;

        if (!string.IsNullOrWhiteSpace(attempt.ProviderCheckoutId))
        {
            var state = await provider.QueryCheckoutAsync(attempt.ProviderCheckoutId, cancellationToken);
            reached = state.Found;
            providerStatus = state.Status;
            if (state.Found)
            {
                await SettleAsync(db, attempt, state, null, realtime, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return Results.Ok(new PaymentStatusDto(
            attempt.Invoice.Id, attempt.Invoice.Status, attempt.Invoice.Status == "paid", providerStatus, reached));
    }

    /// <summary>
    /// Records what the provider sent, then acts on it only after the session
    /// behind it has been read back from the provider's own API.
    ///
    /// The event row is inserted before anything changes. Its unique event id is
    /// what turns a redelivery into a provable duplicate rather than a second pass
    /// over the same invoice.
    /// </summary>
    private static async Task<IResult> WebhookAsync(
        HttpContext http,
        AcademyDbContext db,
        IPaymentProvider provider,
        RealtimeNotifier realtime,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(http.Request, cancellationToken);
        var signature = http.Request.Headers["Stripe-Signature"].FirstOrDefault();
        var callback = provider.DecodeCallback(body, signature, DateTimeOffset.UtcNow);

        if (!callback.Verified || callback.EventId is null)
        {
            // Logged, never stored: an unsigned request would otherwise be a cheap
            // way to fill the audit table.
            logger.LogWarning("Rejected an unverifiable payment callback ({Reason}).", callback.Error);
            return Results.BadRequest(new { message = "Unverified callback." });
        }

        if (!IsActionable(callback.EventType))
            return Results.Ok(new { received = true, ignored = true });

        var row = new PaymentCallbackEvent
        {
            Provider = provider.Name,
            ProviderEventId = callback.EventId,
            ProviderCheckoutId = callback.ProviderReference,
            MerchantReference = callback.MerchantReference,
            EventType = callback.EventType,
            Outcome = "unmatched",
        };

        db.PaymentCallbackEvents.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            logger.LogInformation("Payment callback {Event} was delivered twice.", callback.EventId);
            return Results.Ok(new { received = true });
        }

        if (string.IsNullOrWhiteSpace(callback.ProviderReference))
        {
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { received = true });
        }

        var attempt = await db.PaymentTransactions
            .Include(x => x.Invoice)
            .FirstOrDefaultAsync(
                x => x.ProviderCheckoutId == callback.ProviderReference
                  || x.MerchantReference == (callback.MerchantReference ?? ""),
                cancellationToken);

        if (attempt?.Invoice is null)
        {
            logger.LogWarning("Payment callback named an unknown checkout {Session}.", callback.ProviderReference);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { received = true });
        }

        row.MerchantReference = attempt.MerchantReference;
        var state = await provider.QueryCheckoutAsync(attempt.ProviderCheckoutId ?? callback.ProviderReference!, cancellationToken);

        if (!state.Found)
        {
            row.Outcome = "deferred";
            row.Detail = Clipped(state.Error, 300);
            await db.SaveChangesAsync(cancellationToken);
            // Asking for a retry is the honest answer: the money is not known.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        row.ProviderTransactionId = state.ProviderTransactionId;
        row.AmountCents = state.AmountCents;
        row.Currency = state.Currency;

        row.Outcome = await SettleAsync(db, attempt, state, row, realtime, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A delivery, or the student's own polling, wrote this state first. The
            // retry arrives as a duplicate and changes nothing.
            logger.LogInformation("Payment callback {Event} collided with another write.", callback.EventId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { received = true });
    }

    /// <summary>
    /// Moves one attempt and its invoice to what the provider says, and returns the
    /// outcome recorded against the callback. Shared by the webhook and the
    /// student's own polling, so both paths verify identically.
    ///
    /// It takes an already-read <paramref name="state"/>: whoever calls this has
    /// proven the checkout exists at the provider, and nothing here reads a value
    /// the browser supplied.
    /// </summary>
    internal static async Task<string> SettleAsync(
        AcademyDbContext db,
        PaymentTransaction attempt,
        ProviderCheckoutState state,
        PaymentCallbackEvent? row,
        RealtimeNotifier realtime,
        CancellationToken cancellationToken)
    {
        var invoice = attempt.Invoice
            ??= await db.PaymentInvoices.FirstAsync(x => x.Id == attempt.InvoiceId, cancellationToken);

        if (!state.Paid)
        {
            if (state.Status == "expired" && attempt.Status == "pending")
            {
                attempt.Status = "voided";
                attempt.FailureReason = "The checkout expired before it was paid.";
                if (invoice.Status == "awaiting_payment")
                {
                    invoice.Status = "open";
                    invoice.UpdatedAt = DateTimeOffset.UtcNow;
                    return "ignored";
                }
            }

            return attempt.Status == "captured" ? "applied" : "ignored";
        }

        attempt.ProviderTransactionId ??= state.ProviderTransactionId;
        attempt.PaymentBrand ??= state.PaymentBrand;
        attempt.PaymentMethodType ??= state.PaymentMethodType;
        attempt.PresentmentAmountCents ??= state.PresentmentAmountCents;
        attempt.PresentmentCurrency ??= state.PresentmentCurrency;

        // The two checks that decide whether the academy releases access. Both run
        // against the invoice this server issued, never against anything the
        // student's browser reported. The localised amount a cardholder saw is
        // recorded above and deliberately not compared.
        if (!string.Equals(state.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase))
        {
            attempt.Status = "failed";
            attempt.FailureReason = $"The provider charged in {state.Currency} for a {attempt.Currency} invoice.";
            if (row is not null) row.Detail = "Charged currency differs from the invoice.";
            return "rejected_currency";
        }

        if (state.AmountCents != attempt.AmountCents)
        {
            attempt.Status = "failed";
            attempt.FailureReason = $"The provider charged {state.AmountCents} for a {attempt.AmountCents} invoice.";
            if (row is not null) row.Detail = "Charged amount differs from the invoice.";
            return "rejected_amount";
        }

        attempt.Status = "captured";
        attempt.ConfirmedAt ??= DateTimeOffset.UtcNow;
        attempt.FailureReason = null;


        if (invoice.Status == "paid")
        {
            // Real money against a month already settled. Nothing is released
            // twice; the desk sees the second capture on the transaction.
            if (row is not null) row.Detail = "A second verified payment for an invoice already paid.";
            return "duplicate";
        }

        invoice.Status = "paid";
        invoice.PaidAt ??= DateTimeOffset.UtcNow;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            Action = "payment.captured",
            EntityType = nameof(PaymentInvoice),
            EntityId = invoice.Id,
            Detail = $"Invoice {invoice.Id} for {invoice.Month} settled at {invoice.Amount} {invoice.Currency} (attempt {attempt.Id}).",
        });

        db.Notifications.Add(new AppNotification
        {
            UserId = invoice.StudentId,
            Title = "Payment received",
            Body = $"Your {invoice.Month} plan is paid and ready.",
            Kind = "payment",
            Link = "/student/payments",
            DedupeKey = $"payment:{invoice.Id}",
        });

        _ = realtime.ToUserAsync(invoice.StudentId, RealtimeScopes.Billing);
        return "applied";
    }

    /// <summary>
    /// Whether the month being billed actually held classes. A month whose lessons
    /// were all cancelled is not a month a student bought, so it cannot be billed
    /// and cannot be paid.
    /// </summary>
    private static async Task<bool> HasClassesAsync(
        AcademyDbContext db,
        Guid studentId,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var (start, end) = PaymentEndpoints.MonthBounds(month);
        return await db.Lessons
            .AsNoTracking()
            .AnyAsync(
                x => x.StudentId == studentId
                    && x.Status != "cancelled"
                    && x.StartsAt >= start
                    && x.StartsAt < end,
                cancellationToken);
    }

    private static async Task<int> PlannedDaysAsync(AcademyDbContext db, Guid studentId, CancellationToken cancellationToken)
    {
        var days = await db.StudentOnboardings
            .AsNoTracking()
            .Where(x => x.StudentId == studentId)
            .Select(x => x.PlanDaysPerWeek)
            .FirstOrDefaultAsync(cancellationToken);

        return ApiEndpoints.StudentPlanDaysPerWeek.Contains(days) ? days : 0;
    }

    private static bool IsActionable(string eventType) =>
        eventType is "checkout.session.completed"
            or "checkout.session.async_payment_succeeded"
            or "checkout.session.async_payment_failed"
            or "checkout.session.expired";

    private static string DefaultCurrency(IConfiguration configuration) =>
        configuration["Stripe:DefaultCurrency"]?.Trim().ToUpperInvariant() is { Length: 3 } code ? code : "USD";

    private static string DescriptionFor(PaymentInvoice invoice) =>
        $"Ana Quran Academy · {invoice.Month} · {invoice.DaysPerWeek} days a week";

    private static string Clipped(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // A declared length is required: it is what keeps an unbounded body from
        // being streamed into memory before the signature is even looked at.
        // Kestrel truncates the stream at this length, so a short lie makes the
        // signature fail rather than letting a partial body through.
        if (request.ContentLength is not > 0 || request.ContentLength > StripeProvider.MaxCallbackBytes) return "";

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        return body.Length > StripeProvider.MaxCallbackBytes ? "" : body;
    }

    private static async Task<List<PaymentAttemptDto>> AttemptsAsync(
        AcademyDbContext db, IReadOnlyList<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var rows = await db.PaymentTransactions
            .AsNoTracking()
            .Where(x => invoiceIds.Contains(x.InvoiceId))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToPublicAttempt).ToList();
    }

    internal static PaymentAttemptDto ToPublicAttempt(PaymentTransaction x) =>
        new(x.Id, x.Provider, x.Status, x.AmountCents, x.Currency, x.CreatedAt, x.ConfirmedAt,
            x.PresentmentAmountCents, x.PresentmentCurrency, x.FailureReason);

    internal static InvoiceDto ToInvoice(PaymentInvoice x, IReadOnlyList<PaymentAttemptDto> attempts) =>
        new(x.Id, x.Month, x.DaysPerWeek, x.Currency, x.Amount, x.Status, x.Note, x.IssuedAt, x.PaidAt, attempts);

    private static InvoiceViewDto ToView(PaymentInvoice x, IReadOnlyList<PaymentAttemptDto> attempts) =>
        new(x.Month, x.DaysPerWeek, x.Currency, x.Amount, ToInvoice(x, attempts));
}
