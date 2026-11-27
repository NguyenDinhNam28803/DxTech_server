using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace server.Modules.Payments.Features.Sepay;

/// <summary>
/// Kiểm chữ ký webhook SePay: <c>X-SePay-Signature: sha256=&lt;hex&gt;</c> = HMAC-SHA256(secret, "{timestamp}.{raw body}"),
/// <c>X-SePay-Timestamp</c> = giây Unix. So sánh thời gian hằng; lệch giờ quá ngưỡng ⇒ từ chối (chống phát lại).
/// Tính trên **raw body** đúng như byte nhận được — không parse rồi serialize lại.
/// </summary>
internal static class SepaySignature
{
    public const string SignatureHeader = "X-SePay-Signature";
    public const string TimestampHeader = "X-SePay-Timestamp";

    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        byte[] message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message)).ToLowerInvariant();
    }

    /// <returns>null nếu hợp lệ, ngược lại là lý do (để ghi vào <c>last_error</c>, không trả cho bên gọi).</returns>
    public static string? Verify(string secret, string? signature, string? timestamp, ReadOnlySpan<byte> body, DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return "missing_signature";
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long unix)
            || Math.Abs(now.ToUnixTimeSeconds() - unix) > toleranceSeconds)
        {
            return "stale_or_bad_timestamp";
        }

        byte[] expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        byte[] actual = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? null : "bad_signature";
    }
}
