namespace server.Modules.Identity.Contracts;

/// <summary>Thông tin người dùng cho module khác (Sales: snapshot email/tên lên đơn).</summary>
public interface IUserProfiles
{
    Task<UserProfileRef?> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>Admin đang hoạt động tạo sớm nhất — tác giả mặc định của dữ liệu nạp bằng lệnh (Content: <c>--seed-content</c>).</summary>
    Task<UserProfileRef?> GetOldestActiveAdminAsync(CancellationToken ct);
}

public sealed record UserProfileRef(Guid Id, string Email, string FullName, bool IsActive);
