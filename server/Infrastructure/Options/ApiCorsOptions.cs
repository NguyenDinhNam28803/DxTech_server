using System.ComponentModel.DataAnnotations;

namespace server.Infrastructure.Options;

/// <summary>
/// Danh sách origin được phép gọi API từ trình duyệt (ADR-011): web ở https://dxtechs.com gọi API ở
/// https://api.dxtechs.com — khác origin nên cần CORS. Đọc từ section <c>Cors</c>
/// (biến môi trường <c>Cors__AllowedOrigins__0</c>, <c>Cors__AllowedOrigins__1</c>...).
///
/// API cho phép gửi kèm cookie (refresh token ở Phase 2) nên origin phải được liệt kê đích danh:
/// cấm "*" và cấm origin có đường dẫn. Cấu hình sai ⇒ API không khởi động (ValidateOnStart).
/// </summary>
public sealed class ApiCorsOptions : IValidatableObject
{
    public const string SectionName = "Cors";

    /// <summary>Thời gian trình duyệt được cache kết quả preflight (giảm request OPTIONS).</summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(10);

    [Required(ErrorMessage = "Thiếu Cors:AllowedOrigins.")]
    [MinLength(1, ErrorMessage = "Cors:AllowedOrigins phải có ít nhất một origin.")]
    public string[] AllowedOrigins { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (string origin in AllowedOrigins)
        {
            if (!IsValidOrigin(origin, out string reason))
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins chứa giá trị không hợp lệ '{origin}': {reason}",
                    [nameof(AllowedOrigins)]);
            }
        }
    }

    /// <summary>
    /// Origin hợp lệ: <c>scheme://host[:port]</c>, scheme http/https, không đường dẫn, không query,
    /// không ký tự đại diện. http chỉ chấp nhận cho localhost (máy dev).
    /// </summary>
    public static bool IsValidOrigin(string? origin, out string reason)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            reason = "rỗng.";
            return false;
        }

        if (origin.Contains('*'))
        {
            reason = "không được dùng ký tự đại diện '*' khi API cho phép gửi kèm cookie.";
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            reason = "phải là URL tuyệt đối http:// hoặc https://.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            reason = "chỉ localhost mới được dùng http://; môi trường thật phải là https://.";
            return false;
        }

        if (uri.AbsolutePath != "/" || origin.EndsWith('/') || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            reason = "origin chỉ gồm scheme://host[:port], không có '/' cuối, đường dẫn hay query.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
