namespace server.Modules.Identity.Contracts;

/// <summary>Thông tin người dùng cho module khác (Sales: snapshot email/tên lên đơn).</summary>
public interface IUserProfiles
{
    Task<UserProfileRef?> GetAsync(Guid userId, CancellationToken ct);
}

public sealed record UserProfileRef(Guid Id, string Email, string FullName, bool IsActive);
