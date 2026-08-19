using System.Security.Cryptography;
using System.Text;

namespace MeetingRecord.Business.Services.Other;

public sealed class TotpService : ITotpService
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private const int SecretBytes = 20;

    public string GenerateSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Base32.Encode(bytes);
    }

    public string GenerateProvisioningUri(string secret, string account, string issuer)
    {
        string encodedIssuer = Uri.EscapeDataString(issuer);
        string encodedAccount = Uri.EscapeDataString(account);
        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}" +
               $"?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    public string ComputeCode(string secret, long unixSeconds)
    {
        long counter = unixSeconds / StepSeconds;
        return ComputeHotp(secret, counter);
    }

    public bool VerifyCode(string secret, string code)
        => VerifyCode(secret, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), window: 1);

    public bool VerifyCode(string secret, string code, long unixSeconds, int window)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        code = code.Trim();
        long counter = unixSeconds / StepSeconds;
        for (long offset = -window; offset <= window; offset++)
        {
            string candidate = ComputeHotp(secret, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeHotp(string secret, long counter)
    {
        byte[] key = Base32.Decode(secret);
        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        byte[] hash = HMACSHA1.HashData(key, counterBytes);

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        int otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }
}

/// <summary>RFC 4648 Base32（無填充）編解碼，供 TOTP 密鑰使用。</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var builder = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                bitsLeft -= 5;
                builder.Append(Alphabet[index]);
            }
        }

        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            builder.Append(Alphabet[index]);
        }

        return builder.ToString();
    }

    public static byte[] Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(input.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char c in input)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                continue;
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
