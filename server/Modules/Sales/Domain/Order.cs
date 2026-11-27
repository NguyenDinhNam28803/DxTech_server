using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Sales.Domain;

/// <summary>
/// Đơn hàng — chứng từ (bảng <c>sales.orders</c>). Tiền tính ở server, lưu bigint VND (ADR-006).
/// Sau khi <c>paid</c>, cột tiền và thông tin khách không đổi nữa. Webhook và job hết hạn cùng đổi
/// <see cref="Status"/> ⇒ <c>xmin</c> làm concurrency token (ADR-007). Chuyển trạng thái viết ở PR-11/PR-12.
/// </summary>
internal sealed class Order : IAuditable
{
    /// <summary>Đơn chưa trả tiền tự huỷ sau thời gian này (ADR-013 §5).</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(30);

    public const int MaxLines = 20;

    private readonly List<OrderItem> _items = [];

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private Order()
    {
        Code = null!;
        CustomerEmail = null!;
        CustomerName = null!;
    }

    public Order(Guid userId, Guid idempotencyKey, string customerEmail, string customerName, DateTimeOffset now, string? code = null)
    {
        Id = UuidV7.NewGuid(now);
        Code = code ?? OrderCode.New();
        UserId = userId;
        IdempotencyKey = idempotencyKey;
        CustomerEmail = customerEmail;
        CustomerName = customerName;
        Status = OrderStatus.PendingPayment;
        CreatedAt = now;
        UpdatedAt = now;
        ExpiresAt = now + PaymentWindow;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Header <c>Idempotency-Key</c> của lần đặt hàng — bấm 2 lần không tạo 2 đơn (UNIQUE theo user).</summary>
    public Guid IdempotencyKey { get; private set; }

    public OrderStatus Status { get; set; }

    /// <summary>Snapshot từ tài khoản lúc đặt.</summary>
    public string CustomerEmail { get; private set; }

    public string CustomerName { get; private set; }

    /// <summary>JSON object {companyName, taxCode, address, email} để xuất hoá đơn; null nếu không cần.</summary>
    public string? VatInfo { get; set; }

    public long SubtotalVnd { get; private set; }

    /// <summary>Phase 3 luôn 0 — mã giảm giá ở Phase 4.</summary>
    public long DiscountVnd { get; private set; }

    /// <summary>Số tiền khách phải chuyển; webhook so khớp đúng số này.</summary>
    public long TotalVnd { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? FulfilledAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public OrderCancelReason? CancelReason { get; set; }

    public DateTimeOffset? RefundedAt { get; set; }

    public string? Note { get; set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Huỷ đơn đang chờ thanh toán (khách huỷ, admin huỷ, hết hạn). Key đang giữ do caller trả về kho.</summary>
    public void Cancel(OrderCancelReason reason, DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidOperationException("Chỉ huỷ được đơn đang chờ thanh toán.");
        }

        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
    }

    /// <summary>Thêm một dòng (mỗi gói một dòng) và tính lại tiền. Chỉ khi đơn còn chờ thanh toán.</summary>
    public OrderItem AddItem(
        Guid variantId, string productName, string variantName, string sku, ItemDeliveryType deliveryType, long unitPriceVnd, int quantity)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidOperationException("Chỉ thêm dòng khi đơn đang chờ thanh toán.");
        }

        if (_items.Count >= MaxLines)
        {
            throw new InvalidOperationException($"Đơn tối đa {MaxLines} dòng.");
        }

        if (_items.Any(i => i.VariantId == variantId))
        {
            throw new InvalidOperationException("Mỗi gói chỉ một dòng — gộp số lượng trước khi thêm.");
        }

        var item = new OrderItem(Id, variantId, productName, variantName, sku, deliveryType, unitPriceVnd, quantity);
        _items.Add(item);
        SubtotalVnd = checked(SubtotalVnd + item.LineTotalVnd);
        TotalVnd = SubtotalVnd - DiscountVnd;
        return item;
    }
}
