using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Identity.Domain;

/// <summary>
/// Tài khoản khách hàng / quản trị. Bảng <c>identity.users</c> (thiết kế DB v1.0 mục 6, ADR-012).
/// Không xoá mềm: khoá tài khoản bằng <see cref="Status"/>; yêu cầu xoá dữ liệu cá nhân thực hiện bằng ẩn danh
/// hoá các cột, giữ <see cref="Id"/> để đơn hàng cũ vẫn hợp lệ.
/// </summary>
internal sealed class User : IAuditable
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private User()
    {
        Email = null!;
        PasswordHash = null!;
        FullName = null!;
    }

    /// <param name="email">Đã chuẩn hoá bằng <see cref="EmailAddress.Normalize"/>.</param>
    /// <param name="passwordHash">Chuỗi do <c>PasswordHasher&lt;User&gt;</c> sinh — không bao giờ là mật khẩu rõ.</param>
    public User(string email, string fullName, string passwordHash, UserRole role, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        Email = email;
        FullName = fullName;
        PasswordHash = passwordHash;
        Role = role;
        Status = UserStatus.Active;
        SecurityStamp = NewSecurityStamp();
    }

    public Guid Id { get; private set; }

    /// <summary>Cột <c>citext</c>: so sánh và UNIQUE không phân biệt hoa thường ở chính database.</summary>
    public string Email { get; set; }

    public DateTimeOffset? EmailConfirmedAt { get; set; }

    public string PasswordHash { get; set; }

    public string FullName { get; set; }

    public string? Phone { get; set; }

    public string? CompanyName { get; set; }

    /// <summary>Mã số thuế (10 hoặc 13 số) để gợi ý sẵn khi xuất hoá đơn VAT.</summary>
    public string? TaxCode { get; set; }

    public UserRole Role { get; set; }

    public UserStatus Status { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockoutUntil { get; set; }

    /// <summary>
    /// Đổi khi đổi mật khẩu / role / đăng xuất mọi nơi ⇒ mọi phiên cũ mất hiệu lực (ADR-012 §2, §5).
    /// Ngẫu nhiên hoàn toàn (UUID v4), KHÔNG dùng UUID v7 vì phần đầu v7 là thời gian — đoán được.
    /// </summary>
    public Guid SecurityStamp { get; private set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Concurrency token — cột hệ thống <c>xmin</c>: hai admin đổi role cùng lúc ⇒ người sau nhận 409.</summary>
    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    private static Guid NewSecurityStamp() => Guid.NewGuid();
}
