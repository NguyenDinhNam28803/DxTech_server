using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace server.Modules.Sales.Domain;

/// <summary>
/// Mã đơn <c>DX</c> + 8 ký tự Crockford base32 (ADR-013 §2), ví dụ <c>DX7K3QH9M2</c>.
/// Không có dấu gạch vì nhiều app ngân hàng bỏ ký tự đặc biệt trong nội dung chuyển khoản.
/// 40 bit ngẫu nhiên: không lộ số lượng đơn, không đoán được mã của người khác.
/// Bảng chữ bỏ I, L, O, U (dễ nhầm với 1, 0, V khi đọc qua điện thoại).
/// </summary>
internal static partial class OrderCode
{
    public const string Prefix = "DX";
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int RandomLength = 8;

    /// <summary>Biểu thức dùng cho CHECK trong DB và kiểm tra ở code — một nguồn duy nhất.</summary>
    public const string Pattern = "^DX[0-9A-HJKMNP-TV-Z]{8}$";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[5]; // 40 bit = 8 ký tự × 5 bit
        RandomNumberGenerator.Fill(bytes);
        ulong value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        Span<char> chars = stackalloc char[RandomLength];
        for (int i = RandomLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        return Prefix + new string(chars);
    }

    public static bool IsValid(string? code) => code is not null && CodeRegex().IsMatch(code);

    /// <summary>
    /// Tìm mã đơn trong nội dung chuyển khoản (ADR-013 §2): không phân biệt hoa thường, bỏ khoảng trắng và dấu gạch
    /// mà app ngân hàng hay chèn. Trả về mã chuẩn (chữ hoa) hoặc null.
    /// </summary>
    public static string? FindIn(string? transferContent)
    {
        if (string.IsNullOrWhiteSpace(transferContent))
        {
            return null;
        }

        string compact = new(transferContent.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        Match m = SearchRegex().Match(compact);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex("DX[0-9A-HJKMNP-TV-Z]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex SearchRegex();
}
