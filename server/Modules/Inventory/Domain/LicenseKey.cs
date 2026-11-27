using server.Shared.Ids;

namespace server.Modules.Inventory.Domain;

/// <summary>
/// Một license key trong kho (bảng <c>inventory.license_keys</c>, ADR-014). DB chỉ giữ bản mã hoá AES-256-GCM
/// (<c>nonce ‖ ciphertext ‖ tag</c>), HMAC để chống nhập trùng và 4 ký tự cuối để admin nhận diện.
/// Khoá mã hoá nằm ngoài DB (.env). Mã hoá/giải mã viết ở PR-11 (<c>ILicenseKeyProtector</c>).
/// Cấp phát dùng <c>SELECT … FOR UPDATE SKIP LOCKED</c>; <c>xmin</c> chặn ghi đè đồng thời.
/// </summary>
internal sealed class LicenseKey
{
    public const int HashLength = 32;

    /// <summary>nonce 12 byte + tag 16 byte; ciphertext ít nhất 1 byte.</summary>
    public const int MinEncryptedLength = 12 + 16 + 1;

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private LicenseKey()
    {
        KeyEncrypted = null!;
        KeyHash = null!;
        KeyHint = null!;
    }

    public LicenseKey(Guid variantId, Guid batchId, byte[] keyEncrypted, short keyVersion, byte[] keyHash, string keyHint, DateTimeOffset createdAt)
    {
        if (keyHash.Length != HashLength)
        {
            throw new ArgumentException($"key_hash phải dài {HashLength} byte (HMAC-SHA256).", nameof(keyHash));
        }

        if (keyEncrypted.Length < MinEncryptedLength)
        {
            throw new ArgumentException("Bản mã quá ngắn — thiếu nonce/tag?", nameof(keyEncrypted));
        }

        Id = UuidV7.NewGuid(createdAt);
        VariantId = variantId;
        BatchId = batchId;
        KeyEncrypted = keyEncrypted;
        KeyVersion = keyVersion;
        KeyHash = keyHash;
        KeyHint = keyHint;
        Status = LicenseKeyStatus.Available;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Cũng là một phần AAD khi mã hoá: chép bản mã sang gói khác thì không giải mã được.</summary>
    public Guid VariantId { get; private set; }

    public Guid BatchId { get; private set; }

    public byte[] KeyEncrypted { get; private set; }

    public short KeyVersion { get; private set; }

    public byte[] KeyHash { get; private set; }

    public string KeyHint { get; private set; }

    public LicenseKeyStatus Status { get; private set; }

    public Guid? OrderItemId { get; private set; }

    public DateTimeOffset? ReservedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Reserve(Guid orderItemId, DateTimeOffset now)
    {
        Require(LicenseKeyStatus.Available);
        Status = LicenseKeyStatus.Reserved;
        OrderItemId = orderItemId;
        ReservedAt = now;
    }

    public void Release()
    {
        Require(LicenseKeyStatus.Reserved);
        Status = LicenseKeyStatus.Available;
        OrderItemId = null;
        ReservedAt = null;
    }

    public void Deliver(DateTimeOffset now)
    {
        Require(LicenseKeyStatus.Reserved);
        Status = LicenseKeyStatus.Delivered;
        DeliveredAt = now;
    }

    /// <summary>Key lỗi hoặc hãng thu hồi — giữ nguyên liên kết đơn (nếu đã giao) để truy vết.</summary>
    public void Revoke(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Thu hồi phải có lý do.", nameof(reason));
        }

        Status = LicenseKeyStatus.Revoked;
        RevokedReason = reason;
    }

    private void Require(LicenseKeyStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Key đang ở trạng thái {Status}, cần {expected}.");
        }
    }
}
