namespace server.Modules.Identity.Domain;

/// <summary>Vai trò của tài khoản (ADR-012 §5). Lưu DB: customer | admin. Tự đăng ký luôn là customer.</summary>
internal enum UserRole
{
    Customer,
    Admin,
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
