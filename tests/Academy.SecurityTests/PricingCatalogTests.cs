using Academy.Api;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The price list is what a visitor sees and what a student is billed, so a
/// mistake here is a wrong number on the public page or an invoice the academy
/// cannot honour. These are the rules the catalog is built on.
/// </summary>
public sealed class PricingCatalogTests
{
    private static readonly int[] Tiers = ApiEndpoints.StudentPlanDaysPerWeek;

    private static PricedCatalog Catalog(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, decimal>> prices,
        string defaultCurrency = "USD") => new(prices, defaultCurrency);

    private static Dictionary<int, decimal> Usd() => Tiers.ToDictionary(d => d, d => 30m + d);

    [Fact]
    public void ACurrencyIsOnlyOfferableWhenEveryTierHasAPrice()
    {
        var partial = new Dictionary<int, decimal> { [2] = 30m, [3] = 31m, [4] = 32m };
        var catalog = Catalog(new Dictionary<string, IReadOnlyDictionary<int, decimal>>
        {
            ["USD"] = Usd(),
            ["GBP"] = partial,
        });

        Assert.True(catalog.IsComplete("USD"));
        // A currency the page could only half-price must never reach a student.
        Assert.False(catalog.IsComplete("GBP"));
        Assert.False(catalog.IsComplete("EUR"));
        Assert.Equal(new[] { "USD" }, catalog.CompleteCurrencies());
    }

    [Fact]
    public void APickWithNoCompletePriceListIsIgnoredRatherThanGuessedAt()
    {
        var catalog = Catalog(new Dictionary<string, IReadOnlyDictionary<int, decimal>> { ["USD"] = Usd() });

        Assert.Equal("USD", catalog.ResolveCurrency("usd", null));
        // Explicit request the academy cannot honour: fall through, do not half-quote.
        Assert.Equal("USD", catalog.ResolveCurrency("GBP", "GB"));
        Assert.Equal("USD", catalog.ResolveCurrency(null, "US"));
        Assert.Equal("USD", catalog.ResolveCurrency(null, null));
    }

    [Fact]
    public void AnExplicitPickBeatsTheCountryAndTheCountryBeatsTheDefault()
    {
        var catalog = Catalog(new Dictionary<string, IReadOnlyDictionary<int, decimal>>
        {
            ["USD"] = Usd(),
            ["GBP"] = Tiers.ToDictionary(d => d, d => 25m + d),
        });

        Assert.Equal("GBP", catalog.ResolveCurrency(null, "GB"));
        Assert.Equal("GBP", catalog.ResolveCurrency("gbp", "US"));
        Assert.Equal("USD", catalog.ResolveCurrency(null, "US"));
        Assert.Equal("USD", catalog.ResolveCurrency(null, "EG"));
        Assert.Equal("GBP", catalog.ResolveCurrency(null, "je"));
    }

    [Fact]
    public void NothingAtAllIsOfferedWhenNoCurrencyIsComplete()
    {
        var catalog = Catalog(new Dictionary<string, IReadOnlyDictionary<int, decimal>>
        {
            ["USD"] = new Dictionary<int, decimal> { [2] = 30m },
        });

        Assert.Equal("", catalog.ResolveCurrency("USD", "US"));
        Assert.Empty(catalog.CompleteCurrencies());
    }

    [Theory]
    [InlineData("GB", "GBP")]
    [InlineData("IM", "GBP")]
    [InlineData("DE", "EUR")]
    [InlineData("GR", "EUR")]
    [InlineData("US", null)]
    [InlineData("EG", null)]
    [InlineData("", null)]
    [InlineData("GBR", null)]
    [InlineData("g b", null)]
    public void TheCountryPicksALocalCurrencyOnlyWhereTheAcademyPricesOne(string country, string? expected) =>
        Assert.Equal(expected, PricingCatalog.CurrencyForCountry(country));

    [Theory]
    [InlineData("gbp", "GBP")]
    [InlineData(" USD ", "USD")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void CodesAreNormalisedTheSameWayEverywhere(string? raw, string expected) =>
        Assert.Equal(expected, PricingCatalog.Normalize(raw));

    [Theory]
    [InlineData(38.00, 3800)]
    [InlineData(0.01, 1)]
    [InlineData(44.5, 4450)]
    // A price is exact to the cent; anything finer is rounded away from zero
    // rather than quietly dropped, which is what the provider would charge.
    [InlineData(38.005, 3801)]
    [InlineData(38.004, 3800)]
    [InlineData(0.005, 1)]
    public void AmountsBecomeMinorUnitsWithoutDrift(double amount, int expectedCents) =>
        Assert.Equal(expectedCents, PricingCatalog.ToCents((decimal)amount));

    [Fact]
    public void MinorUnitsComeBackAsTheFigureShown()
    {
        Assert.Equal(38.00m, PricingCatalog.FromCents(3800));
        Assert.Equal(0.01m, PricingCatalog.FromCents(1));
    }

    [Theory]
    [InlineData(2, 8)]
    [InlineData(3, 12)]
    [InlineData(5, 20)]
    public void TheMonthHoldsFourClassesPerWeeklyDay(int days, int classes) =>
        Assert.Equal(classes, PricingCatalog.ClassesPerMonth(days));

    [Fact]
    public void APriceIsOnlyReadOutWhenItExists()
    {
        var catalog = Catalog(new Dictionary<string, IReadOnlyDictionary<int, decimal>> { ["USD"] = Usd() });

        Assert.True(catalog.TryGetPrice("USD", 2, out var amount));
        Assert.Equal(32m, amount);
        Assert.False(catalog.TryGetPrice("USD", 9, out _));
        Assert.False(catalog.TryGetPrice("GBP", 2, out _));
        Assert.Equal(0m, catalog.PriceOrZero("GBP", 2));
    }
}
