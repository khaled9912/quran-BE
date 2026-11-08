using Academy.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Internal;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Academy.SecurityTests;

/// <summary>
/// The code stores behind phone sign-in and password reset. These run without a
/// database so the caps and cooldowns are asserted on their own.
/// </summary>
public sealed class CodeStoreGuardTests
{
    [Fact]
    public void DevCodeIsRefusedWhenTheMachineIsNotInDevelopment()
    {
        // The flag alone must not be enough: a mislabeled compose stack sets
        // Otp:DevMode=true and would otherwise accept 8888 for anyone.
        var otp = NewOtp("Production", devMode: true);

        Assert.False(otp.DevMode);
        Assert.False(otp.Check("+96170000001", "8888"));
    }

    [Fact]
    public void DevCodeIsRefusedWhenTheFlagIsOffEvenInDevelopment()
    {
        var otp = NewOtp("Development", devMode: false);

        Assert.False(otp.DevMode);
        Assert.False(otp.Check("+96170000002", "8888"));
    }

    [Fact]
    public void ResetDevCodeUsesTheSameDoubleGate()
    {
        Assert.False(NewReset("Production", devMode: true).DevMode);
        Assert.False(NewReset("Development", devMode: false).DevMode);
        Assert.True(NewReset("Development", devMode: true).DevMode);
    }

    [Fact]
    public void FifthWrongCodeDestroysTheEntryRatherThanKeepingItGuessable()
    {
        var otp = NewOtp("Production", devMode: false);
        var phone = "+96170000003";
        var code = otp.TryIssue(phone)!;

        for (var attempt = 0; attempt < 5; attempt++) Assert.False(otp.Check(phone, "000001"));

        // The cap removes the entry, so the real code no longer works either.
        Assert.False(otp.Check(phone, code));
    }

    [Fact]
    public void CorrectCodePassesUntilItIsConsumed()
    {
        var otp = NewOtp("Production", devMode: false);
        var phone = "+96170000004";
        var code = otp.TryIssue(phone)!;

        Assert.True(otp.Check(phone, code));
        otp.Consume(phone);

        Assert.False(otp.Check(phone, code));
    }

    [Fact]
    public void ResendWithinTheCooldownIssuesNothing()
    {
        var otp = NewOtp("Production", devMode: false);
        var phone = "+96170000005";

        Assert.NotNull(otp.TryIssue(phone));
        Assert.Null(otp.TryIssue(phone));
    }

    [Fact]
    public void ResetCodesAreScopedToTheAddressTheyWereIssuedFor()
    {
        var resets = NewReset("Production", devMode: false);
        var code = resets.TryIssue("a@example.test")!;

        Assert.True(resets.Check("a@example.test", code));
        Assert.False(resets.Check("b@example.test", code));
    }

    [Fact]
    public void PhoneNumbersThatAreNotInternationalAreRejected()
    {
        Assert.Null(PhoneNumbers.Normalize(null));
        Assert.Null(PhoneNumbers.Normalize("12345"));
        Assert.Equal("+96170000006", PhoneNumbers.Normalize("00961 70 000 006"));
    }

    private static PhoneOtpStore NewOtp(string environment, bool devMode) =>
        new(NewConfig(("Otp:DevMode", devMode.ToString())), new FakeEnvironment(environment));

    private static PasswordResetStore NewReset(string environment, bool devMode) =>
        new(NewConfig(("PasswordReset:DevMode", devMode.ToString())), new FakeEnvironment(environment));

    private static IConfiguration NewConfig(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Academy.SecurityTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileInfo ContentRootFile { get; set; } = null!;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
