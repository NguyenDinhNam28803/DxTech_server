namespace server.Modules.Inventory.Contracts;

/// <summary>Tồn kho license key cho module khác (Sales: báo giá; Catalog: nhãn "còn hàng").</summary>
public interface IStockQuery
{
    /// <summary>
    /// Số key <c>available</c> theo gói, **tối đa <paramref name="cap"/>** — không lộ số tồn thật ra ngoài.
    /// Gói không có key không có trong kết quả (tức là 0). Dùng index một phần <c>ix_license_keys_available</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountAvailableAsync(IReadOnlyCollection<Guid> variantIds, int cap, CancellationToken ct);
}
