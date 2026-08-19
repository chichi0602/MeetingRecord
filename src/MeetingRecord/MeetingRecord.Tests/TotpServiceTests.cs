using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

public sealed class TotpServiceTests
{
    // RFC 6238 測試向量：ASCII 密鑰 "12345678901234567890" 的 Base32。
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    public void ComputeCode_ShouldMatchRfc6238Vectors(long unixSeconds, string expected)
    {
        var service = new TotpService();

        var code = service.ComputeCode(RfcSecret, unixSeconds);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void VerifyCode_WithCorrectCode_ShouldReturnTrue()
    {
        var service = new TotpService();

        Assert.True(service.VerifyCode(RfcSecret, "287082", 59L, window: 1));
    }

    [Fact]
    public void VerifyCode_WithWrongCode_ShouldReturnFalse()
    {
        var service = new TotpService();

        Assert.False(service.VerifyCode(RfcSecret, "000000", 59L, window: 1));
    }

    [Fact]
    public void VerifyCode_WithinTimeWindow_ShouldAcceptPreviousStepCode()
    {
        var service = new TotpService();
        // T=59 落在 step 1；step 1 的碼在 T=89（step 2）以 window=1 仍應被接受。
        Assert.True(service.VerifyCode(RfcSecret, "287082", 89L, window: 1));
    }

    [Fact]
    public void GenerateSecret_ShouldReturnUsableBase32()
    {
        var service = new TotpService();

        var secret = service.GenerateSecret();

        Assert.False(string.IsNullOrWhiteSpace(secret));
        Assert.All(secret, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
        // 可用於計算驗證碼（不拋例外、6 碼）。
        Assert.Equal(6, service.ComputeCode(secret, 59L).Length);
    }

    [Fact]
    public void GenerateProvisioningUri_ShouldContainOtpauthAndParameters()
    {
        var service = new TotpService();

        var uri = service.GenerateProvisioningUri("JBSWY3DPEHPK3PXP", "alice@example.com", "MeetingRecord");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("issuer=MeetingRecord", uri);
    }
}
