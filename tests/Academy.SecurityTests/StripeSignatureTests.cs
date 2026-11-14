using Academy.Api;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// Stripe's webhook signature, checked against a value computed outside this
/// codebase. Everything that marks an invoice paid hangs on this comparison, so
/// it is tested with an input no part of the academy produced.
/// </summary>
public sealed class StripeSignatureTests
{
    private const string Secret = "whsec_test_secret";

    /// <summary>Body, timestamp and mac from an HMAC-SHA256 over "t.body" computed independently.</summary>
    private const string Body = """{"id":"evt_1h4","type":"checkout.session.completed"}""";

    private const long SignedAt = 1_763_000_000;
    private const string Expected = "da74e4ddc879871ef701b692cf588833891ba3dd997c7ff58dcb3d3b05ea7028";

    private static readonly DateTimeOffset OnTheDot =
        DateTimeOffset.FromUnixTimeSeconds(SignedAt).AddSeconds(1);

    private static bool Verify(string body, string? header, string secret, DateTimeOffset receivedAt) =>
        StripeSignature.Verify(body, header, secret, receivedAt, out _);

    [Fact]
    public void TheAcademyComputesWhatTheProviderComputes() =>
        Assert.Equal(Expected, StripeSignature.Compute($"{SignedAt}.{Body}", Secret));

    [Fact]
    public void AProperlySignedCallbackPasses() =>
        Assert.True(Verify(Body, $"t={SignedAt},v1={Expected}", Secret, OnTheDot));

    [Fact]
    public void AnEditedBodyFails() =>
        Assert.False(Verify(
            Body.Replace("evt_1h4", "evt_attacker"),
            $"t={SignedAt},v1={Expected}",
            Secret,
            OnTheDot));

    [Fact]
    public void AChangedAmountInsideTheBodyFails() =>
        Assert.False(Verify(
            """{"id":"evt_1h4","type":"checkout.session.completed","data":{"object":{"amount_total":1}}}""",
            $"t={SignedAt},v1={Expected}",
            Secret,
            OnTheDot));

    [Fact]
    public void ASecretBelongingToAnotherEndpointFails() =>
        Assert.False(Verify(Body, $"t={SignedAt},v1={Expected}", "whsec_someone_elses", OnTheDot));

    [Fact]
    public void AMissingOrUnreadableHeaderFails()
    {
        Assert.False(Verify(Body, null, Secret, OnTheDot));
        Assert.False(Verify(Body, "", Secret, OnTheDot));
        Assert.False(Verify(Body, "t=1763000000", Secret, OnTheDot));
        Assert.False(Verify(Body, "v1=" + Expected, Secret, OnTheDot));
        Assert.False(Verify(Body, "hello,world", Secret, OnTheDot));
    }

    [Fact]
    public void AnEmptySigningSecretCanVerifyNothing() =>
        Assert.False(Verify(Body, $"t={SignedAt},v1={Expected}", "", OnTheDot));

    [Fact]
    public void AReplayOfAnOldSignedBodyFails()
    {
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(SignedAt);

        Assert.False(Verify(Body, $"t={SignedAt},v1={Expected}", Secret, signedAt.Add(StripeSignature.Tolerance).AddSeconds(1)));
        // A callback claiming to be signed in the future is just as unusable.
        Assert.False(Verify(Body, $"t={SignedAt},v1={Expected}", Secret, signedAt.Add(-StripeSignature.Tolerance).AddSeconds(-1)));
    }

    [Fact]
    public void TheBoundaryOfTheToleranceStillPasses()
    {
        var exactly = DateTimeOffset.FromUnixTimeSeconds(SignedAt).Add(StripeSignature.Tolerance);
        Assert.True(Verify(Body, $"t={SignedAt},v1={Expected}", Secret, exactly));
    }

    [Fact]
    public void EitherPublishedKeyPassesDuringASecretRotation()
    {
        var other = StripeSignature.Compute($"{SignedAt}.{Body}", "whsec_the_old_one");
        Assert.True(Verify(Body, $"t={SignedAt},v1={other},v1={Expected}", Secret, OnTheDot));
        Assert.True(Verify(Body, $"t={SignedAt},v1={Expected},v1={other}", Secret, OnTheDot));
    }

    [Fact]
    public void OnlyAGoodMacAmongSeveralFails()
    {
        var forged = new string('0', Expected.Length);
        Assert.False(Verify(Body, $"t={SignedAt},v1={forged},v1={forged}", Secret, OnTheDot));
        // A mac of a different length must not be compared byte-for-byte at all.
        Assert.False(Verify(Body, $"t={SignedAt},v1=abc", Secret, OnTheDot));
    }

    [Fact]
    public void AMacOfTheWrongLengthFails() =>
        Assert.False(Verify(Body, $"t={SignedAt},v1={Expected.ToUpperInvariant()}x", Secret, OnTheDot));
}
