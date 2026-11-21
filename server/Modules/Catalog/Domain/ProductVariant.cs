using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Catalog.Domain;

/// <summary>
/// Gói bán của một sản phẩm ("12 tháng · 6 người dùng"), mang giá. Bảng <c>catalog.product_variants</c>.
/// Tiền là <c>long</c> đơn vị VND (ADR-006, D5). Ghi đè đồng thời được chặn bằng cột hệ thống <c>xmin</c>
/// (D9): người lưu sau nhận <c>DbUpdateConcurrencyException</c> ⇒ API trả 409.
/// </summary>
internal sealed class ProductVariant : IAuditable, ISoftDeletable
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private ProductVariant()
    {
        Sku = null!;
        Name = null!;
    }

    public ProductVariant(Guid productId, string sku, string name, long priceVnd, DeliveryType deliveryType, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        ProductId = productId;
        Sku = sku;
        Name = name;
        PriceVnd = priceVnd;
        DeliveryType = deliveryType;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; private set; }

    /// <summary>Mã gói, duy nhất trong các gói chưa xoá.</summary>
    public string Sku { get; set; }

    public string Name { get; set; }

    /// <summary>Thời hạn tính bằng tháng; null = vĩnh viễn.</summary>
    public int? TermMonths { get; set; }

    /// <summary>Số người dùng/thiết bị.</summary>
    public int Seats { get; set; } = 1;

    public long PriceVnd { get; set; }

    /// <summary>Giá gốc để hiển thị gạch ngang; phải lớn hơn <see cref="PriceVnd"/> nếu có.</summary>
    public long? CompareAtPriceVnd { get; set; }

    public DeliveryType DeliveryType { get; set; }

    /// <summary>Gói hiển thị mặc định; mỗi sản phẩm tối đa một gói mặc định (partial unique index).</summary>
    public bool IsDefault { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Concurrency token, map vào cột hệ thống <c>xmin</c> — PostgreSQL tự đổi mỗi lần UPDATE.</summary>
    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
