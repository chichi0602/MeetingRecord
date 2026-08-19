namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 以 RFC 6238（TOTP，HMAC-SHA1、30 秒週期、6 碼）產生與驗證動態驗證碼。
/// </summary>
public interface ITotpService
{
    /// <summary>產生新的 Base32 密鑰（供使用者綁定驗證器 App）。</summary>
    string GenerateSecret();

    /// <summary>產生 otpauth:// 佈建 URI，供驗證器 App 掃描 QR Code。</summary>
    string GenerateProvisioningUri(string secret, string account, string issuer);

    /// <summary>依指定 Unix 時間（秒）計算當下的 TOTP 驗證碼。</summary>
    string ComputeCode(string secret, long unixSeconds);

    /// <summary>驗證使用者輸入的驗證碼（以目前時間，允許前後各一個時間窗）。</summary>
    bool VerifyCode(string secret, string code);

    /// <summary>驗證使用者輸入的驗證碼（指定時間與時間窗，供測試與精確控制）。</summary>
    bool VerifyCode(string secret, string code, long unixSeconds, int window);
}
