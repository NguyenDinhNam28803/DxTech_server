using System.Net;
using server.Shared.Ids;

namespace server.Modules.Identity.Domain;

/// <summary>
/// Một refresh token trong chuỗi xoay vòng (ADR-012 §1). Bảng <c>identity.refresh_tokens</c>.
/// Chỉ lưu SHA-256 của token (32 byte); token gốc chỉ nằm trong cookie HttpOnly của trình duyệt.
/// Logic phát hành / xoay vòng / phát hiện dùng lại nằm ở PR-8; PR-7 chỉ tạo bảng và ràng buộc.
/// </summary>
internal sealed class RefreshToken
{
    public const int HashLength = 32;

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private RefreshToken()
    {
        TokenHash = null!;
    }

    public RefreshToken(
        Guid userId,
        byte[] tokenHash,
        Guid familyId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset sessionExpiresAt,
        Guid? id = null)
    {
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"Hash refresh token phải dài {HashLength} byte (SHA-256).", nameof(tokenHash));
        }

        Id = id ?? UuidV7.NewGuid(createdAt);
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        SessionExpiresAt = sessionExpiresAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public User? User { get; private set; }

    public byte[] TokenHash { get; private set; }

    /// <summary>Mọi token sinh ra từ cùng một lần đăng nhập — để thu hồi cả họ khi phát hiện dùng lại.</summary>
    public Guid FamilyId { get; private set; }

    /// <summary>Token mới thay thế token này khi xoay vòng.</summary>
    public Guid? ReplacedById { get; set; }

    /// <summary>Hạn trượt (14 ngày), không vượt <see cref="SessionExpiresAt"/>.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Hạn tuyệt đối của cả phiên (đăng nhập + 30 ngày), chép sang mọi token cùng họ.</summary>
    public DateTimeOffset SessionExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public RefreshTokenRevokedReason? RevokedReason { get; private set; }

    public IPAddress? CreatedIp { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Revoke(RefreshTokenRevokedReason reason, DateTimeOffset at)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = at;
        RevokedReason = reason;
    }
}
