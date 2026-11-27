namespace server.Modules.Catalog.Contracts;

/// <summary>
/// Giá và thông tin gói đang bán được — cho module khác (Sales: báo giá giỏ hàng, tạo đơn).
/// "Đang bán được" dùng chung điều kiện với trang sản phẩm: gói bật + chưa xoá, sản phẩm và danh mục bật + chưa xoá.
/// </summary>
public interface IVariantPricing
{
    /// <summary>Một truy vấn cho cả giỏ. Id không tồn tại / đang ẩn không có trong kết quả.</summary>
    Task<IReadOnlyDictionary<Guid, PurchasableVariant>> GetPurchasableAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct);
}

/// <param name="DeliveryType">license_key | account | manual.</param>
public sealed record PurchasableVariant(
    Guid VariantId,
    string ProductSlug,
    string ProductName,
    string VariantName,
    string Sku,
    string DeliveryType,
    long PriceVnd,
    long? CompareAtPriceVnd);

/// <summary>Giá trị <see cref="PurchasableVariant.DeliveryType"/> — một nguồn cho module khác so sánh.</summary>
public static class DeliveryTypes
{
    public const string LicenseKey = "license_key";
    public const string Account = "account";
    public const string Manual = "manual";
}
