using System.ComponentModel.DataAnnotations;
using System.Text;

namespace server.Infrastructure.Options;

/// <summary>
/// Cấu hình xác thực (ADR-012), section <c>Auth</c>. Khoá ký JWT KHÔNG nằm trong appsettings production:
/// production lấy từ biến môi trường <c>Auth__SigningKeys__0</c> (= <c>AUTH_SIGNING_KEY</c> trong <c>.env</c>),
/// khoá cũ khi xoay vòng ở <c>Auth__SigningKeys__1</c>. Thiếu khoá hoặc khoá ngắn ⇒ API không khởi động.
/// </summary>
public sealed class AuthOptions : IValidatableObject
{
    public const string SectionName = "Auth";

    /// <summary>Tối thiểu 32 byte (256 bit) cho HMAC-SHA256.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>Khoá ký: vị trí 0 dùng để ký, mọi vị trí dùng để kiểm tra. Mục rỗng bị bỏ qua.</summary>
    public string[] SigningKeys { get; set; } = [];

    [Required]
    public string Issuer { get; set; } = "https://api.dxtechs.com";

    [Required]
    public string Audience { get; set; } = "dxtechs-web";

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Hạn trượt của refresh token.</summary>
    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Hạn tuyệt đối của một phiên đăng nhập (cả họ token).</summary>
    [Range(1, 180)]
    public int SessionDays { get; set; } = 30;

    /// <summary>Token vừa xoay vòng bị dùng lại trong khoảng này ⇒ 409 (hai tab), không coi là đánh cắp.</summary>
    [Range(0, 60)]
    public int ReuseGraceSeconds { get; set; } = 10;

    [Range(1, 50)]
    public int MaxFailedLogins { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Kết quả kiểm tra admin trong DB được nhớ bao lâu (0 = kiểm tra mọi request).</summary>
    [Range(0, 300)]
    public int AdminCheckCacheSeconds { get; set; } = 30;

    public AuthRateLimits RateLimits { get; set; } = new();

    public IReadOnlyList<string> ActiveSigningKeys => SigningKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        IReadOnlyList<string> keys = ActiveSigningKeys;
        if (keys.Count == 0)
        {
            yield return new ValidationResult(
                "Thiếu Auth:SigningKeys:0 (production: AUTH_SIGNING_KEY trong .env, tạo bằng: openssl rand -base64 48).",
                [nameof(SigningKeys)]);
        }

        foreach (string key in keys)
        {
            if (Encoding.UTF8.GetByteCount(key) < MinKeyBytes)
            {
                yield return new ValidationResult(
                    $"Auth:SigningKeys có khoá ngắn hơn {MinKeyBytes} byte — dùng: openssl rand -base64 48.",
                    [nameof(SigningKeys)]);
            }
        }

        if (keys.Distinct().Count() != keys.Count)
        {
            yield return new ValidationResult("Auth:SigningKeys có hai khoá trùng nhau.", [nameof(SigningKeys)]);
        }

        if (SessionDays < RefreshTokenDays)
        {
            yield return new ValidationResult("Auth:SessionDays phải ≥ RefreshTokenDays.", [nameof(SessionDays)]);
        }
    }
}

/// <summary>Giới hạn số request mỗi IP (ADR-012 §3). Test đặt giá trị khác qua cấu hình.</summary>
public sealed class AuthRateLimits
{
    public int LoginPerMinute { get; set; } = 10;
    public int LoginPerHour { get; set; } = 50;
    public int RegisterPerHour { get; set; } = 5;
    public int RefreshPerMinute { get; set; } = 60;
    public int OtherPerMinute { get; set; } = 30;

    /// <summary>POST /api/cart/quote (PR-10) — giỏ hàng gọi lại mỗi khi đổi số lượng.</summary>
    public int CartQuotePerMinute { get; set; } = 60;

    /// <summary>Tất cả request tới /api/orders (đặt hàng) mỗi IP mỗi giờ (PR-11, chống giữ kho).</summary>
    public int OrdersPerHour { get; set; } = 10;
}
