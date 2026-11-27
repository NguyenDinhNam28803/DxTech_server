namespace server.Modules.Inventory.Contracts;

/// <summary>
/// Giữ / trả / giao license key cho module Sales (phase3-schema.md mục 3.7). Mọi thao tác chạy trong transaction
/// hiện tại của <c>AppDbContext</c> (ADR-002) ⇒ tạo đơn + giữ key là một khối, lỗi thì rollback cả hai.
/// </summary>
public interface IKeyReservation
{
    /// <summary>
    /// Giữ tối đa <paramref name="quantity"/> key <c>available</c> của gói cho một dòng đơn (FIFO,
    /// <c>FOR UPDATE SKIP LOCKED</c>: hai đơn đồng thời không lấy trùng key, không chờ nhau). Trả về số key giữ được;
    /// ít hơn yêu cầu ⇒ caller phải rollback.
    /// </summary>
    Task<int> ReserveAsync(Guid variantId, Guid orderItemId, int quantity, DateTimeOffset now, CancellationToken ct);

    /// <summary>Đơn huỷ / hết hạn: trả mọi key đang <c>reserved</c> của các dòng đơn về kho. Trả về số key đã trả.</summary>
    Task<int> ReleaseAsync(IReadOnlyCollection<Guid> orderItemIds, CancellationToken ct);

    /// <summary>Tiền về: chuyển key <c>reserved</c> → <c>delivered</c> (PR-12). Trả về số key đã giao.</summary>
    Task<int> DeliverAsync(IReadOnlyCollection<Guid> orderItemIds, DateTimeOffset now, CancellationToken ct);

    /// <summary>Key rõ đã giao của các dòng đơn — chỉ gọi khi đã xác thực chủ đơn. Không ghi log giá trị.</summary>
    Task<IReadOnlyList<DeliveredKey>> GetDeliveredKeysAsync(IReadOnlyCollection<Guid> orderItemIds, CancellationToken ct);
}

public sealed record DeliveredKey(Guid OrderItemId, string Key, DateTimeOffset DeliveredAt);
