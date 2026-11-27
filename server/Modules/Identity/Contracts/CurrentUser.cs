using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace server.Modules.Identity.Contracts;

/// <summary>Đọc người dùng đã xác thực từ access token cho module khác (không lộ chi tiết claim của Identity).</summary>
public static class CurrentUser
{
    /// <summary>Id người dùng (claim <c>sub</c>); null khi chưa đăng nhập.</summary>
    public static Guid? IdOf(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out Guid id) ? id : null;
}
