using System.Text.RegularExpressions;

namespace server.Modules.Catalog.Features.Products;

/// <summary>
/// Đổi chuỗi người dùng gõ thành biểu thức <c>tsquery</c> an toàn: chỉ giữ chữ và số (mọi ký tự đặc biệt của
/// tsquery như <c>&amp; | ! : * ( ) '</c> bị loại), mỗi từ khớp theo tiền tố (<c>:*</c>, để gõ dở "phan me" vẫn ra
/// "phần mềm"), các từ nối bằng AND. Bỏ dấu tiếng Việt làm ở SQL bằng <c>catalog.f_unaccent</c> để khớp đúng
/// cách cột <c>search_vector</c> được tạo.
/// </summary>
public static partial class SearchText
{
    public const int MaxTerms = 8;
    public const int MaxTermLength = 40;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>"Phần mềm  đồ-họa!!" → "phần:* &amp; mềm:* &amp; đồ:* &amp; họa:*"; không có từ nào → null.</summary>
    public static string? ToTsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        List<string> terms = Word().Matches(input)
            .Select(m => m.Value.Length > MaxTermLength ? m.Value[..MaxTermLength] : m.Value)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .Take(MaxTerms)
            .ToList();

        return terms.Count == 0 ? null : string.Join(" & ", terms.Select(t => t + ":*"));
    }
}
