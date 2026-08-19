using System.Security.Cryptography;

namespace MeetingRecord.Business.Helpers;

public enum PasswordVerificationOutcome
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2,
}

/// <summary>
/// 以 PBKDF2 (SHA256) 產生與驗證密碼雜湊；並可辨識舊 SHA256 格式以觸發自動升級。
/// 新格式為自描述字串：PBKDF2.SHA256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;。
/// </summary>
public static class SecurePasswordHasher
{
    private const string Prefix = "PBKDF2.SHA256";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static PasswordVerificationOutcome VerifyPassword(string password, string storedPassword, string? legacySalt)
    {
        if (string.IsNullOrEmpty(storedPassword))
        {
            return PasswordVerificationOutcome.Failed;
        }

        if (storedPassword.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return VerifyNewFormat(password, storedPassword)
                ? PasswordVerificationOutcome.Success
                : PasswordVerificationOutcome.Failed;
        }

        // 舊格式（SHA256 + Salt）：驗證成功時要求重新雜湊升級。
        string legacyHash = PasswordHelper.GetPasswordSHA(legacySalt ?? string.Empty, password);
        return string.Equals(legacyHash, storedPassword, StringComparison.Ordinal)
            ? PasswordVerificationOutcome.SuccessRehashNeeded
            : PasswordVerificationOutcome.Failed;
    }

    private static bool VerifyNewFormat(string password, string storedPassword)
    {
        string[] parts = storedPassword.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
