namespace server.Modules.Identity.Domain;

/// <summary>
/// Vai trò của tài khoản (ADR-012 §5, ADR-015 §4). Lưu DB: customer | editor | admin. Tự đăng ký luôn là customer;
/// editor/admin chỉ cấp qua CLI (<c>--create-admin</c>, <c>--set-role</c>).
/// </summary>
internal enum UserRole
{
    Customer,
    /// <summary>Biên tập nội dung (module Content): không thấy đơn hàng, thanh toán, key, người dùng.</summary>
    Editor,
    Admin,
}

/// <summary>Tên role trong token/API — trùng giá trị lưu DB (snake_case của enum).</summary>
internal static class UserRoles
{
    public static string Name(UserRole role)
        => server.Infrastructure.Persistence.Conversions.SnakeCaseEnumConverter<UserRole>.ToProvider(role);

    public static bool TryParse(string? value, out UserRole role)
    {
        foreach (UserRole r in Enum.GetValues<UserRole>())
        {
            if (string.Equals(Name(r), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                role = r;
                return true;
            }
        }

        role = default;
        return false;
    }
}

/// <summary>
/// Trạng thái tài khoản. Lưu DB: active | locked | disabled.
/// <c>locked</c> = admin khoá; <c>disabled</c> = đã ẩn danh hoá theo yêu cầu xoá dữ liệu (NĐ 13).
/// Khoá tạm vì đăng nhập sai không đổi trạng thái — dùng <c>lockout_until</c>.
/// </summary>
internal enum UserStatus
{
    Active,
    Locked,
    Disabled,
}

/// <summary>Lý do thu hồi refresh token (ADR-012 §1). Lưu DB dạng snake_case.</summary>
internal enum RefreshTokenRevokedReason
{
    /// <summary>Đã được thay bằng token mới khi refresh.</summary>
    Rotated,
    Logout,
    /// <summary>Token đã xoay vòng bị dùng lại sau thời gian ân hạn ⇒ thu hồi cả họ.</summary>
    ReuseDetected,
    PasswordChanged,
    Admin,
}
