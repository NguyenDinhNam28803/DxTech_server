using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Inventory.Contracts;
using server.Modules.Sales.Contracts;
using server.Modules.Sales.Domain;

namespace server.Modules.Sales.Features.Orders;

/// <summary>
/// Luật áp tiền vào đơn (ADR-013 §4, PR-12). Chạy trong transaction của caller; khoá dòng đơn
/// (<c>SELECT … FOR UPDATE</c>) để webhook, job hết hạn và khách huỷ không đè lên nhau.
/// <list type="table">
/// <item>Đơn chờ, đủ/thừa tiền ⇒ <c>paid</c> → giao key → <c>fulfilled</c> (nếu mọi dòng là license_key).</item>
/// <item>Đơn chờ, thiếu tiền ⇒ <c>underpaid</c>, đơn giữ nguyên.</item>
/// <item>Đơn huỷ vì hết hạn ≤ 24 giờ, đủ tiền, giữ lại được đủ key ⇒ mở lại (<c>late_payment</c>); thiếu key ⇒ <c>late_no_stock</c>.</item>
/// <item>Đơn đã trả / huỷ vì lý do khác / quá 24 giờ ⇒ <c>order_not_payable</c> (admin hoàn tiền).</item>
/// </list>
/// </summary>
internal sealed class OrderPayments(AppDbContext db, IKeyReservation keys, ILogger<OrderPayments> logger) : IOrderPayments
{
    public static readonly TimeSpan LatePaymentWindow = TimeSpan.FromHours(24);

    public async Task<PaymentApplication> ApplyAsync(string transferContent, long amountVnd, DateTimeOffset now, CancellationToken ct)
    {
        string? code = OrderCode.FindIn(transferContent);
        if (code is null)
        {
            return new PaymentApplication("no_order_code", null, null);
        }

        // Khoá dòng đơn trước khi đọc bằng EF (cùng transaction).
        List<Guid> locked = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM sales.orders WHERE code = {code} FOR UPDATE
            """).ToListAsync(ct);
        if (locked.Count == 0)
        {
            return new PaymentApplication("unknown_order", null, code);
        }

        Order order = await db.Set<Order>().Include(o => o.Items).SingleAsync(o => o.Id == locked[0], ct);
        string paidStatus = amountVnd == order.TotalVnd ? "matched" : "overpaid";

        switch (order.Status)
        {
            case OrderStatus.PendingPayment when amountVnd < order.TotalVnd:
                return new PaymentApplication("underpaid", order.Id, code);

            case OrderStatus.PendingPayment:
                MarkPaid(order, OrderStatus.PendingPayment, now, reason: null);
                await DeliverAsync(order, now, ct);
                return new PaymentApplication(paidStatus, order.Id, code);

            case OrderStatus.Cancelled when order.CancelReason == OrderCancelReason.Expired
                                          && order.CancelledAt is { } cancelledAt && now - cancelledAt <= LatePaymentWindow:
                if (amountVnd < order.TotalVnd)
                {
                    return new PaymentApplication("underpaid", order.Id, code);
                }

                if (!await ReserveAgainAsync(order, now, ct))
                {
                    return new PaymentApplication("late_no_stock", order.Id, code);
                }

                order.CancelledAt = null;
                order.CancelReason = null;
                MarkPaid(order, OrderStatus.Cancelled, now, reason: "late_payment");
                await DeliverAsync(order, now, ct);
                return new PaymentApplication(paidStatus, order.Id, code);

            default:
                return new PaymentApplication("order_not_payable", order.Id, code);
        }
    }

    private void MarkPaid(Order order, OrderStatus from, DateTimeOffset now, string? reason)
    {
        order.Status = OrderStatus.Paid;
        order.PaidAt = now;
        db.Add(new OrderStatusHistory(order.Id, from, OrderStatus.Paid, OrderActorType.Webhook, now, reason: reason));
    }

    /// <summary>Đơn đã huỷ thì key đã về kho ⇒ giữ lại đủ cho mọi dòng license_key, thiếu thì trả hết những key vừa giữ.</summary>
    private async Task<bool> ReserveAgainAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        var reserved = new List<Guid>();
        foreach (OrderItem item in order.Items.Where(i => i.DeliveryType == ItemDeliveryType.LicenseKey))
        {
            int got = await keys.ReserveAsync(item.VariantId, item.Id, item.Quantity, now, ct);
            reserved.Add(item.Id);
            if (got < item.Quantity)
            {
                await keys.ReleaseAsync(reserved, ct);
                return false;
            }
        }

        return true;
    }

    /// <summary>Giao key đang giữ; mọi dòng là license_key và đủ key ⇒ đơn <c>fulfilled</c>. Có dòng account/manual ⇒ dừng ở <c>paid</c>, admin giao.</summary>
    private async Task DeliverAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        List<OrderItem> keyed = order.Items.Where(i => i.DeliveryType == ItemDeliveryType.LicenseKey).ToList();
        int expected = keyed.Sum(i => i.Quantity);
        int delivered = await keys.DeliverAsync(keyed.Select(i => i.Id).ToArray(), now, ct);
        if (delivered != expected)
        {
            // Không xảy ra khi giữ key đúng; nếu có: dừng ở "paid" để admin xử lý, không giao thiếu mà báo hoàn tất.
            logger.LogError("Order {Code}: delivered {Delivered}/{Expected} key(s) — left as paid", order.Code, delivered, expected);
            return;
        }

        foreach (OrderItem item in keyed)
        {
            item.FulfilledAt = now;
        }

        if (keyed.Count == order.Items.Count)
        {
            order.Status = OrderStatus.Fulfilled;
            order.FulfilledAt = now;
            db.Add(new OrderStatusHistory(order.Id, OrderStatus.Paid, OrderStatus.Fulfilled, OrderActorType.System, now, reason: "keys_delivered"));
        }
    }
}
