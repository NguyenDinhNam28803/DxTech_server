using server.Modules.Catalog.Contracts;
using server.Modules.Inventory.Contracts;
using server.Modules.Sales.Domain;
using server.Shared.Results;

namespace server.Modules.Sales.Features.Cart;

/// <summary>Một dòng giỏ hàng từ trình duyệt — chỉ id gói và số lượng; giá client gửi (nếu có) bị bỏ qua.</summary>
public sealed record CartItemInput(Guid VariantId, int Quantity);

public sealed record CartQuoteRequest(IReadOnlyList<CartItemInput>? Items);

/// <param name="Status">ok | insufficient_stock | out_of_stock | unavailable.</param>
/// <param name="AvailableQuantity">Chỉ khi thiếu hàng; tối đa 20 — không lộ tồn kho thật.</param>
public sealed record CartQuoteLine(
    Guid VariantId,
    string Status,
    int Quantity,
    string? ProductSlug,
    string? ProductName,
    string? VariantName,
    string? Sku,
    string? DeliveryType,
    long? UnitPriceVnd,
    long? CompareAtPriceVnd,
    long? LineTotalVnd,
    int? AvailableQuantity);

public sealed record CartQuote(
    IReadOnlyList<CartQuoteLine> Lines,
    long SubtotalVnd,
    long DiscountVnd,
    long TotalVnd,
    bool CanCheckout,
    DateTimeOffset QuotedAt);

public static class CartLineStatus
{
    public const string Ok = "ok";
    public const string InsufficientStock = "insufficient_stock";
    public const string OutOfStock = "out_of_stock";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Báo giá giỏ hàng (PR-10, thiết kế mục B). Giỏ lưu ở trình duyệt nhưng mọi con số do server tính.
/// Dòng không hợp lệ không làm hỏng cả request — giỏ cũ trong trình duyệt cần biết dòng nào để báo khách.
/// PR-11 dùng lại <see cref="QuoteAsync"/> khi tạo đơn ⇒ giá ở giỏ và trên đơn cùng một lõi.
/// Hai truy vấn mỗi request, không phụ thuộc số dòng.
/// </summary>
public sealed class QuoteCartHandler
{
    public const int MaxLines = Order.MaxLines;

    private readonly IVariantPricing _pricing;
    private readonly IStockQuery _stock;
    private readonly TimeProvider _time;

    internal QuoteCartHandler(IVariantPricing pricing, IStockQuery stock, TimeProvider time)
    {
        _pricing = pricing;
        _stock = stock;
        _time = time;
    }

    public async Task<Result<CartQuote>> HandleAsync(CartQuoteRequest request, CancellationToken ct)
    {
        Result<IReadOnlyList<CartItemInput>> merged = Validate(request);
        if (merged.IsFailure)
        {
            return merged.Error;
        }

        return await QuoteAsync(merged.Value, ct);
    }

    /// <summary>Gộp dòng trùng gói, kiểm tra giới hạn. Lỗi ⇒ 400 có lỗi theo trường.</summary>
    internal static Result<IReadOnlyList<CartItemInput>> Validate(CartQuoteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        IReadOnlyList<CartItemInput> items = request.Items ?? [];
        if (items.Count == 0)
        {
            errors["items"] = ["Giỏ hàng trống."];
        }
        else if (items.Count > MaxLines)
        {
            errors["items"] = [$"Giỏ hàng tối đa {MaxLines} dòng."];
        }

        for (int i = 0; i < items.Count && errors.Count == 0; i++)
        {
            if (items[i].VariantId == Guid.Empty)
            {
                errors[$"items[{i}].variantId"] = ["Thiếu mã gói."];
            }

            if (items[i].Quantity is < 1 or > OrderItem.MaxQuantity)
            {
                errors[$"items[{i}].quantity"] = [$"Số lượng từ 1 đến {OrderItem.MaxQuantity}."];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("sales.cart_invalid", "Giỏ hàng không hợp lệ.", errors);
        }

        // Gộp theo thứ tự xuất hiện đầu tiên; gộp xong vượt giới hạn ⇒ báo ở dòng đầu tiên của gói đó.
        var mergedItems = new List<CartItemInput>();
        foreach (IGrouping<Guid, (CartItemInput Item, int Index)> g in items.Select((it, i) => (it, i)).GroupBy(x => x.it.VariantId))
        {
            int quantity = g.Sum(x => x.Item.Quantity);
            if (quantity > OrderItem.MaxQuantity)
            {
                return Error.Validation("sales.cart_invalid", "Giỏ hàng không hợp lệ.", new Dictionary<string, string[]>
                {
                    [$"items[{g.First().Index}].quantity"] = [$"Tổng số lượng của một gói tối đa {OrderItem.MaxQuantity}."],
                });
            }

            mergedItems.Add(new CartItemInput(g.Key, quantity));
        }

        return mergedItems;
    }

    internal async Task<CartQuote> QuoteAsync(IReadOnlyList<CartItemInput> items, CancellationToken ct)
    {
        Guid[] ids = items.Select(i => i.VariantId).ToArray();
        IReadOnlyDictionary<Guid, PurchasableVariant> variants = await _pricing.GetPurchasableAsync(ids, ct);
        Guid[] keyed = variants.Values.Where(v => v.DeliveryType == DeliveryTypes.LicenseKey).Select(v => v.VariantId).ToArray();
        IReadOnlyDictionary<Guid, int> stock = await _stock.CountAvailableAsync(keyed, OrderItem.MaxQuantity, ct);

        var lines = new List<CartQuoteLine>(items.Count);
        long subtotal = 0;
        foreach (CartItemInput item in items)
        {
            if (!variants.TryGetValue(item.VariantId, out PurchasableVariant? v))
            {
                lines.Add(new CartQuoteLine(item.VariantId, CartLineStatus.Unavailable, item.Quantity,
                    null, null, null, null, null, null, null, null, null));
                continue;
            }

            string status = CartLineStatus.Ok;
            int? available = null;
            if (v.DeliveryType == DeliveryTypes.LicenseKey)
            {
                int n = stock.GetValueOrDefault(v.VariantId);
                if (n == 0)
                {
                    status = CartLineStatus.OutOfStock;
                    available = 0;
                }
                else if (n < item.Quantity)
                {
                    status = CartLineStatus.InsufficientStock;
                    available = n;
                }
            }

            long lineTotal = checked(v.PriceVnd * item.Quantity);
            if (status == CartLineStatus.Ok)
            {
                subtotal = checked(subtotal + lineTotal);
            }

            lines.Add(new CartQuoteLine(v.VariantId, status, item.Quantity, v.ProductSlug, v.ProductName, v.VariantName, v.Sku,
                v.DeliveryType, v.PriceVnd, v.CompareAtPriceVnd, lineTotal, available));
        }

        return new CartQuote(lines, subtotal, 0, subtotal, lines.All(l => l.Status == CartLineStatus.Ok), _time.GetUtcNow());
    }
}
