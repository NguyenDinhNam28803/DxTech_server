using Microsoft.Extensions.Options;
using server.Infrastructure.Options;

namespace server.Modules.Identity.Auth;

/// <summary>
/// Cookie chứa refresh token (ADR-012 §1): <c>__Secure-dxt_rt; HttpOnly; Secure; SameSite=Strict; Path=/api/auth</c>,
/// host-only trên api.dxtechs.com. JavaScript không đọc được; trình duyệt chỉ gửi kèm request tới /api/auth/*.
/// </summary>
internal static class RefreshTokenCookie
{
    public const string Name = "__Secure-dxt_rt";
    public const string Path = "/api/auth";

    public static void Write(HttpResponse response, string rawToken, DateTimeOffset expiresAt)
        => response.Cookies.Append(Name, rawToken, Options(expiresAt));

    public static void Delete(HttpResponse response)
        => response.Cookies.Delete(Name, Options(DateTimeOffset.UnixEpoch));

    public static string? Read(HttpRequest request)
        => request.Cookies.TryGetValue(Name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt,
        IsEssential = true,
    };
}

/// <summary>
/// CSRF cho endpoint dùng cookie (ADR-012 §4): ngoài SameSite=Strict, header <c>Origin</c> phải nằm trong
/// <c>Cors:AllowedOrigins</c>. Trình duyệt luôn gửi Origin cho POST; thiếu hoặc lạ ⇒ 403.
/// </summary>
internal sealed class OriginGuard(IOptions<ApiCorsOptions> cors)
{
    public bool IsAllowed(HttpRequest request)
    {
        string? origin = request.Headers.Origin;
        return !string.IsNullOrEmpty(origin) && cors.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}
