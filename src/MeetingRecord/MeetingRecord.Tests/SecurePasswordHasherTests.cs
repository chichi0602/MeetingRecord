using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

public sealed class SecurePasswordHasherTests
{
    [Fact]
    public void HashPassword_ShouldProduceSelfDescribingHash_DifferentFromRawPassword()
    {
        var hash = SecurePasswordHasher.HashPassword("my-password");

        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.NotEqual("my-password", hash);
        Assert.StartsWith("PBKDF2", hash);
    }

    [Fact]
    public void HashPassword_ShouldBeNonDeterministic_ForSamePassword()
    {
        var first = SecurePasswordHasher.HashPassword("my-password");
        var second = SecurePasswordHasher.HashPassword("my-password");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_AgainstNewHash_ShouldSucceed()
    {
        var hash = SecurePasswordHasher.HashPassword("my-password");

        var result = SecurePasswordHasher.VerifyPassword("my-password", hash, legacySalt: null);

        Assert.Equal(PasswordVerificationOutcome.Success, result);
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_AgainstNewHash_ShouldFail()
    {
        var hash = SecurePasswordHasher.HashPassword("my-password");

        var result = SecurePasswordHasher.VerifyPassword("wrong-password", hash, legacySalt: null);

        Assert.Equal(PasswordVerificationOutcome.Failed, result);
    }

    [Fact]
    public void VerifyPassword_WithCorrectLegacyPassword_ShouldRequestRehash()
    {
        var salt = Guid.NewGuid().ToString();
        var legacyStored = PasswordHelper.GetPasswordSHA(salt, "my-password");

        var result = SecurePasswordHasher.VerifyPassword("my-password", legacyStored, legacySalt: salt);

        Assert.Equal(PasswordVerificationOutcome.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_WithWrongLegacyPassword_ShouldFail()
    {
        var salt = Guid.NewGuid().ToString();
        var legacyStored = PasswordHelper.GetPasswordSHA(salt, "my-password");

        var result = SecurePasswordHasher.VerifyPassword("wrong-password", legacyStored, legacySalt: salt);

        Assert.Equal(PasswordVerificationOutcome.Failed, result);
    }
}
