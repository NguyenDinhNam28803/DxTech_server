using server.Modules.Payments.Contracts;
using server.Modules.Sales.Features.Cart;

namespace server.Modules.Sales.Features.Orders;

/// <param name="VatInfo">Tuỳ chọn: thông tin xuất hoá đơn.</param>
public sealed record CreateOrderRequest(IReadOnlyList<CartItemInput>? Items, string? Note, VatInfoInput? VatInfo);

public sealed record VatInfoInput(string? CompanyName, string? TaxCode, string? Address, string? Email);

public sealed record OrderLineView(
    Guid Id, Guid VariantId, string ProductName, string VariantName, string Sku, string DeliveryType,
    long UnitPriceVnd, int Quantity, long LineTotalVnd, IReadOnlyList<string> Keys);

public sealed record OrderEventView(string? FromStatus, string ToStatus, string ActorType, string? Reason, DateTimeOffset At);

/// <param name="Payment">Hướng dẫn chuyển khoản — chỉ khi đơn đang chờ thanh toán.</param>
/// <param name="Lines">Key rõ chỉ có khi đơn đã giao (<c>fulfilled</c>), chỉ trả cho chủ đơn.</param>
public sealed record OrderView(
    string Code,
    string Status,
    long SubtotalVnd,
    long DiscountVnd,
    long TotalVnd,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? FulfilledAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string? Note,
    IReadOnlyList<OrderLineView> Lines,
    IReadOnlyList<OrderEventView> Timeline,
    BankTransferInstruction? Payment);

public sealed record OrderSummary(string Code, string Status, long TotalVnd, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, string? NextCursor);
