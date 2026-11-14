using Microsoft.EntityFrameworkCore;

namespace Academy.Api;

/// <summary>
/// One read of the price table, resolved against the currencies this host may
/// charge in. Both the public plans page and invoice issuing go through this, so
/// the figure a visitor sees and the figure a student is billed cannot drift.
/// </summary>
public sealed record PricedCatalog(
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, decimal>> Prices,
    string DefaultCurrency)
{
    /// <summary>A currency is only offerable when every tier has a price in it.</summary>
    public bool IsComplete(string currency) =>
        Prices.TryGetValue(currency, out var tiers)
        && ApiEndpoints.StudentPlanDaysPerWeek.All(d => tiers.ContainsKey(d));

    public bool TryGetPrice(string currency, int daysPerWeek, out decimal amount)
    {
        amount = 0m;
        return Prices.TryGetValue(currency, out var tiers) && tiers.TryGetValue(daysPerWeek, out amount);
    }

    public decimal PriceOrZero(string currency, int daysPerWeek) =>
        TryGetPrice(currency, daysPerWeek, out var amount) ? amount : 0m;

    /// <summary>Every tier the academy can currently quote in, for the currency switcher.</summary>
    public IReadOnlyList<string> CompleteCurrencies() =>
        Prices.Keys.Where(IsComplete).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The currency to show and bill in. A student's own pick wins, then the
    /// currency of the country they are browsing from, then the academy's default.
    /// A pick with no complete price list is ignored rather than guessed at, so a
    /// page never displays a number the server cannot honour.
    /// </summary>
    public string ResolveCurrency(string? requested, string? country)
    {
        foreach (var candidate in new[] { requested, PricingCatalog.CurrencyForCountry(country), DefaultCurrency })
        {
            var code = PricingCatalog.Normalize(candidate);
            if (code.Length > 0 && IsComplete(code)) return code;
        }

        return "";
    }
}

public static class PricingCatalog
{
    /// <summary>
    /// Countries whose students are shown a local price. Everything else is
    /// quoted in the default currency, which for this academy is US dollars.
    ///
    /// Only two-decimal currencies appear here. Amounts travel as minor units
    /// throughout this codebase, so a zero-decimal currency such as JPY would need
    /// its own exponent table before it could be priced safely.
    /// </summary>
    private static readonly (string Currency, string[] Countries)[] LocalCurrencies =
    [
        ("GBP", ["GB", "IM", "JE", "GG"]),
        ("EUR", ["AT", "BE", "CY", "DE", "EE", "ES", "FI", "FR", "GR", "HR", "IE", "IT",
                 "LT", "LU", "LV", "MT", "NL", "PT", "SK", "SI"]),
    ];

    public static string Normalize(string? currency) =>
        currency?.Trim().ToUpperInvariant() ?? "";

    public static string? CurrencyForCountry(string? country)
    {
        var code = country?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return null;
        return LocalCurrencies.FirstOrDefault(x => x.Countries.Contains(code)).Currency;
    }

    /// <summary>Rounding is deliberate, not incidental: a plan price is exact to the cent.</summary>
    public static int ToCents(decimal amount) =>
        checked((int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));

    public static decimal FromCents(int cents) => cents / 100m;

    /// <summary>Classes a month holds at a tier: the plans page promise, restated here so the invoice can name it.</summary>
    public static int ClassesPerMonth(int daysPerWeek) => daysPerWeek * 4;

    public static async Task<PricedCatalog> LoadAsync(
        AcademyDbContext db, StripeOptions options, string defaultCurrency, CancellationToken cancellationToken)
    {
        var rows = await db.PlanPrices
            .AsNoTracking()
            .Where(x => options.Currencies.Contains(x.Currency))
            .Select(x => new { x.Currency, x.DaysPerWeek, x.MonthlyAmount })
            .ToListAsync(cancellationToken);

        var prices = new Dictionary<string, IReadOnlyDictionary<int, decimal>>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(x => Normalize(x.Currency)))
            prices[group.Key] = group
                .GroupBy(x => x.DaysPerWeek)
                .ToDictionary(x => x.Key, x => x.Max(y => y.MonthlyAmount))
                .AsReadOnly();

        return new PricedCatalog(prices, Normalize(defaultCurrency));
    }
}
