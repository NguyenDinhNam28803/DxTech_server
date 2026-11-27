using server.Shared.Ids;

namespace server.Modules.Sales.Domain;

/// <summary>
/// Dòng đơn (bảng <c>sales.order_items</c>). Chụp lại tên, SKU, giá, cách giao tại lúc đặt — admin sửa gói sau đó
/// không làm đổi chứng từ cũ. Giá lấy từ DB ở server, không bao giờ nhận từ client.
/// </summary>
internal sealed class OrderItem
{
    public const int MaxQuantity = 20;

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private OrderItem()
    {
        ProductNameSnapshot = null!;
        VariantNameSnapshot = null!;
        SkuSnapshot = null!;
    }

    internal OrderItem(
        Guid orderId, Guid variantId, string productName, string variantName, string sku,
        ItemDeliveryType deliveryType, long unitPriceVnd, int quantity)
    {
        if (quantity is < 1 or > MaxQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Số lượng 1–{MaxQuantity}.");
        }

        if (unitPriceVnd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPriceVnd), unitPriceVnd, "Giá không âm.");
        }

        Id = UuidV7.NewGuid();
        OrderId = orderId;
        VariantId = variantId;
        ProductNameSnapshot = productName;
        VariantNameSnapshot = variantName;
        SkuSnapshot = sku;
        DeliveryType = deliveryType;
        UnitPriceVnd = unitPriceVnd;
        Quantity = quantity;
        LineTotalVnd = checked(unitPriceVnd * quantity);
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>FK sang <c>catalog.product_variants</c> (cùng DB — ADR-002); gói xoá mềm vẫn trỏ được.</summary>
    public Guid VariantId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string VariantNameSnapshot { get; private set; }

    public string SkuSnapshot { get; private set; }

    public ItemDeliveryType DeliveryType { get; private set; }

    public long UnitPriceVnd { get; private set; }

    public int Quantity { get; private set; }

    public long LineTotalVnd { get; private set; }

    public DateTimeOffset? FulfilledAt { get; set; }
}
