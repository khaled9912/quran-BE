using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

public sealed record PlanPriceDto(int DaysPerWeek, string Currency, decimal MonthlyAmount, DateTimeOffset UpdatedAt, string? UpdatedByName);

public sealed record SetPlanPriceRequest(int DaysPerWeek, string Currency, decimal MonthlyAmount);

public sealed record RefundRequest(decimal Amount, string? Reason);

public sealed record VoidInvoiceRequest(string Reason);

public sealed record CallbackEventDto(
    Guid Id,
    string Provider,
    string EventType,
    string Outcome,
    string? ProviderEventId,
    string? ProviderCheckoutId,
    string? MerchantReference,
    int AmountCents,
    string Currency,
    string? Detail,
    DateTimeOffset ReceivedAt);

/// <summary>
/// What the desk sees and does about card payments: the price list every invoice
/// is drawn from, the attempts and their outcomes, refunds against a verified
/// payment, and the callback log that proves what the provider told us.
/// </summary>
public static class AdminPaymentsEndpoints
{
    public static void MapAdminPayments(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/payments").RequireAuthorization("AdminOnly");

        admin.MapGet("/prices", ListPricesAsync);
        admin.MapPut("/prices", SavePriceAsync);
        admin.MapGet("/invoices", ListInvoicesAsync);
        admin.MapPost("/invoices/{id:guid}/void", VoidInvoiceAsync);
        admin.MapGet("/transactions", ListTransactionsAsync);
        admin.MapPost("/transactions/{id:guid}/refund", RefundAsync);
        admin.MapGet("/callbacks", ListCallbacksAsync);
    }

    /// <summary>
    /// Every price the academy can quote, including currencies a student cannot
    /// currently be charged in: the desk needs to see an unenabled row rather than
    /// wonder why a figure never appears.
    ///
    /// The tiers and the enabled currencies come from the server too, so the desk
    /// cannot offer a price for a plan that does not exist or a currency the
    /// payment account would refuse.
    /// </summary>
    private static async Task<IResult> ListPricesAsync(
        AcademyDbContext db,
        StripeOptions options,
        CancellationToken cancellationToken)
    {
        var rows = await db.PlanPrices
            .AsNoTracking()
            .OrderBy(x => x.Currency)
            .ThenBy(x => x.DaysPerWeek)
            .ToListAsync(cancellationToken);

        var names = await EditorNamesAsync(db, rows.Select(x => x.UpdatedBy), cancellationToken);

        return Results.Ok(new
        {
            Tiers = ApiEndpoints.StudentPlanDaysPerWeek,
            Currencies = options.Currencies,
            Prices = rows.Select(x => new PlanPriceDto(
                x.DaysPerWeek, x.Currency, x.MonthlyAmount, x.UpdatedAt,
                x.UpdatedBy is null ? null : names.GetValueOrDefault(x.UpdatedBy.Value))),
        });
    }

    private static async Task<IResult> SavePriceAsync(
        SetPlanPriceRequest request,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        StripeOptions options,
        CancellationToken cancellationToken)
    {
        var actorId = ApiEndpoints.UserId(principal);
        var currency = PricingCatalog.Normalize(request.Currency);

        if (!ApiEndpoints.StudentPlanDaysPerWeek.Contains(request.DaysPerWeek))
            return Results.BadRequest(new { message = "Choose one of the published plans." });

        if (currency.Length != 3 || !options.SupportsCurrency(currency))
            return Results.BadRequest(new { message = "That currency is not enabled on the payment account." });

        if (request.MonthlyAmount <= 0m || request.MonthlyAmount > 100000m)
            return Results.BadRequest(new { message = "Enter a monthly price between 0 and 100,000." });

        // Two decimals at most: amounts travel as minor units, and a third decimal
        // would be silently dropped at the provider.
        if (decimal.Round(request.MonthlyAmount, 2) != request.MonthlyAmount)
            return Results.BadRequest(new { message = "A price may carry at most two decimals." });

        var row = await db.PlanPrices.FirstOrDefaultAsync(
            x => x.DaysPerWeek == request.DaysPerWeek && x.Currency == currency, cancellationToken);

        var before = row?.MonthlyAmount.ToString();

        if (row is null)
        {
            db.PlanPrices.Add(new PlanPrice
            {
                DaysPerWeek = request.DaysPerWeek,
                Currency = currency,
                MonthlyAmount = request.MonthlyAmount,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actorId,
            });
        }
        else
        {
            row.MonthlyAmount = request.MonthlyAmount;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = actorId;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = before is null ? "plan-price.created" : "plan-price.updated",
            EntityType = nameof(PlanPrice),
            Detail = $"{request.DaysPerWeek} days a week in {currency}: {(before ?? "none")} to {request.MonthlyAmount}.",
        });

        await db.SaveChangesAsync(cancellationToken);

        // A price change must not reach a bill that is already out.
        var priced = await db.PaymentInvoices.CountAsync(
            x => x.DaysPerWeek == request.DaysPerWeek && x.Currency == currency && x.Status != "paid" && x.Status != "voided",
            cancellationToken);

        return Results.Ok(new
        {
            DaysPerWeek = request.DaysPerWeek,
            Currency = currency,
            MonthlyAmount = request.MonthlyAmount,
            OpenInvoicesUsingOldPrice = priced,
        });
    }

    private static async Task<IResult> ListInvoicesAsync(
        string? month,
        string? status,
        AcademyDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.PaymentInvoices.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (PaymentEndpoints.TryParseMonth(month, out var target))
        {
            var key = PaymentEndpoints.MonthKey(target);
            query = query.Where(x => x.Month == key);
        }

        var invoices = await query
            .Include(x => x.Student)
            .OrderByDescending(x => x.IssuedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var attempts = await AttemptsByInvoiceAsync(db, invoices.Select(x => x.Id), cancellationToken);

        return Results.Ok(invoices.Select(x => new
        {
            x.Id,
            x.Month,
            x.StudentId,
            Student = x.Student?.FullName ?? "",
            x.DaysPerWeek,
            x.Currency,
            x.Amount,
            x.Status,
            x.IssuedAt,
            x.PaidAt,
            x.VoidedAt,
            x.VoidReason,
            Attempts = attempts.GetValueOrDefault(x.Id, []),
        }));
    }

    /// <summary>
    /// Cancels a bill that has not been paid. A paid invoice is settled by a
    /// refund, never by deleting the record of it.
    /// </summary>
    private static async Task<IResult> VoidInvoiceAsync(
        Guid id,
        VoidInvoiceRequest request,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        CancellationToken cancellationToken)
    {
        var actorId = ApiEndpoints.UserId(principal);
        var invoice = await db.PaymentInvoices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (invoice is null) return Results.NotFound();

        if (string.IsNullOrWhiteSpace(request.Reason))
            return Results.BadRequest(new { message = "Say why this invoice is being cancelled." });

        if (invoice.Status == "paid")
            return Results.Conflict(new { message = "This month is already paid. Refund the payment instead." });

        var openAttempts = await db.PaymentTransactions
            .Where(x => x.InvoiceId == invoice.Id && x.Status == "pending")
            .ToListAsync(cancellationToken);

        foreach (var attempt in openAttempts)
        {
            attempt.Status = "voided";
            attempt.FailureReason = "The invoice was cancelled.";
        }

        invoice.Status = "voided";
        invoice.VoidedBy = actorId;
        invoice.VoidedAt = DateTimeOffset.UtcNow;
        invoice.VoidReason = Clipped(request.Reason, 500);
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = "invoice.voided",
            EntityType = nameof(PaymentInvoice),
            EntityId = invoice.Id,
            Detail = $"Invoice {invoice.Id} for {invoice.Month} ({invoice.Amount} {invoice.Currency}) cancelled: {invoice.VoidReason}",
        });

        // The month is billable again: the unique index ignores voided rows, so a
        // corrected invoice can be issued straight away.
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(CheckoutEndpoints.ToInvoice(invoice, []));
    }

    private static async Task<IResult> ListTransactionsAsync(
        string? status,
        AcademyDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.PaymentTransactions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var rows = await query
            .Include(x => x.Invoice)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(x => new
        {
            x.Id,
            x.InvoiceId,
            Month = x.Invoice?.Month ?? "",
            Student = x.Invoice?.StudentId,
            x.Provider,
            x.MerchantReference,
            x.ProviderCheckoutId,
            x.ProviderTransactionId,
            x.AmountCents,
            x.Currency,
            x.PresentmentAmountCents,
            x.PresentmentCurrency,
            x.Status,
            x.RefundedAmountCents,
            x.PaymentBrand,
            x.PaymentMethodType,
            x.FailureReason,
            x.CreatedAt,
            x.ConfirmedAt,
        }));
    }

    /// <summary>
    /// Sends money back against one verified payment.
    ///
    /// The idempotency key carries the running total, so the same amount submitted
    /// twice reaches the provider once: a double refund is a loss the academy
    /// cannot see coming.
    /// </summary>
    private static async Task<IResult> RefundAsync(
        Guid id,
        RefundRequest request,
        ClaimsPrincipal principal,
        AcademyDbContext db,
        IPaymentProvider provider,
        RealtimeNotifier realtime,
        CancellationToken cancellationToken)
    {
        var actorId = ApiEndpoints.UserId(principal);
        var attempt = await db.PaymentTransactions
            .Include(x => x.Invoice)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (attempt?.Invoice is null) return Results.NotFound();
        if (attempt.Status is not ("captured" or "refunded"))
            return Results.Conflict(new { message = "Only a verified payment can be refunded." });

        if (string.IsNullOrWhiteSpace(attempt.ProviderTransactionId))
            return Results.Conflict(new { message = "This payment was never confirmed by the provider." });

        if (!provider.IsConfigured)
            return Results.Problem("Card refunds are not available yet.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var remaining = attempt.AmountCents - attempt.RefundedAmountCents;
        if (remaining <= 0) return Results.Conflict(new { message = "This payment is fully refunded already." });

        var cents = PricingCatalog.ToCents(decimal.Round(request.Amount, 2));
        if (cents <= 0) return Results.BadRequest(new { message = "Enter an amount to refund." });
        if (cents > remaining)
            return Results.BadRequest(new { message = $"At most {PricingCatalog.FromCents(remaining)} can still be refunded." });

        var key = $"refund-{attempt.Id:N}-{attempt.RefundedAmountCents + cents}";
        var result = await provider.RefundAsync(attempt.ProviderTransactionId, cents, key, cancellationToken);

        if (!result.Accepted)
            return Results.Problem(
                Clipped(result.Error, 300) is { Length: > 0 } error ? error : "The refund could not be sent.",
                statusCode: StatusCodes.Status502BadGateway);

        attempt.RefundedAmountCents += cents;
        attempt.Status = attempt.RefundedAmountCents >= attempt.AmountCents ? "refunded" : "captured";

        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = "payment.refunded",
            EntityType = nameof(PaymentTransaction),
            EntityId = attempt.Id,
            Detail = $"{PricingCatalog.FromCents(cents)} {attempt.Currency} refunded from attempt {attempt.Id} "
                + $"({attempt.RefundedAmountCents} of {attempt.AmountCents} cents total). "
                + (string.IsNullOrWhiteSpace(request.Reason) ? "" : Clipped(request.Reason, 200)),
        });

        db.Notifications.Add(new AppNotification
        {
            UserId = attempt.Invoice.StudentId,
            Title = "Refund sent",
            Body = $"{PricingCatalog.FromCents(cents)} {attempt.Currency} is on its way back to your card.",
            Kind = "payment",
            Link = "/student/payments",
            DedupeKey = $"refund:{attempt.Id}:{attempt.RefundedAmountCents}",
        });

        await db.SaveChangesAsync(cancellationToken);
        _ = realtime.ToUserAsync(attempt.Invoice.StudentId, RealtimeScopes.Billing);

        return Results.Ok(new
        {
            attempt.Id,
            attempt.Status,
            attempt.RefundedAmountCents,
            RemainingCents = attempt.AmountCents - attempt.RefundedAmountCents,
        });
    }

    private static async Task<IResult> ListCallbacksAsync(
        string? outcome,
        AcademyDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.PaymentCallbackEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(outcome)) query = query.Where(x => x.Outcome == outcome);

        var rows = await query
            .OrderByDescending(x => x.ReceivedAt)
            .Take(200)
            .Select(x => new CallbackEventDto(
                x.Id, x.Provider, x.EventType, x.Outcome, x.ProviderEventId, x.ProviderCheckoutId,
                x.MerchantReference, x.AmountCents, x.Currency, x.Detail, x.ReceivedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(rows);
    }

    private static async Task<Dictionary<Guid, List<PaymentAttemptDto>>> AttemptsByInvoiceAsync(
        AcademyDbContext db, IEnumerable<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var ids = invoiceIds.ToList();
        var rows = await db.PaymentTransactions
            .AsNoTracking()
            .Where(x => ids.Contains(x.InvoiceId))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Select(CheckoutEndpoints.ToPublicAttempt).ToList());
    }

    private static async Task<Dictionary<Guid, string>> EditorNamesAsync(
        AcademyDbContext db, IEnumerable<Guid?> actorIds, CancellationToken cancellationToken)
    {
        var ids = actorIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.Users
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);
    }

    private static string Clipped(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
