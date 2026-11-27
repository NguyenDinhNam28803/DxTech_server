using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Contracts;
using server.Modules.Inventory.Contracts;
using server.Modules.Payments.Contracts;
using server.Modules.Sales.Domain;
using server.Modules.Sales.Features.Cart;
using server.Shared.Results;

namespace server.Modules.Sales.Features.Orders;

/// <summary>Kết quả tạo đơn: <see cref="Created"/> = false khi trả lại đơn đã tạo trước đó với cùng Idempotency-Key.</summary>
public sealed record CreateOrderResult(OrderView Order, bool Created);

/// <summary>
/// Tạo đơn (PR-11, ADR-013, phase3-schema.md mục 2–4). Một transaction:
/// khoá theo người dùng (advisory lock) → idempotency → giới hạn đơn chờ/ngày → báo giá lại ở server (cùng lõi PR-10)
/// → ghi đơn + dòng + lịch sử → giữ key (SKIP LOCKED). Thiếu key ⇒ ROLLBACK, không còn key nào bị giữ.
/// </summary>
public sealed partial class CreateOrderHandler
{
    private readonly AppDbContext _db;
    private readonly QuoteCartHandler _quote;
    private readonly IKeyReservation _keys;
    private readonly IUserProfiles _users;
    private readonly IPaymentInstructions _payments;
    private readonly SalesOptions _options;
    private readonly TimeProvider _time;

    internal CreateOrderHandler(
        AppDbContext db, QuoteCartHandler quote, IKeyReservation keys, IUserProfiles users, IPaymentInstructions payments,
        IOptions<SalesOptions> options, TimeProvider time)
    {
        _db = db;
        _quote = quote;
        _keys = keys;
        _users = users;
        _payments = payments;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<CreateOrderResult>> HandleAsync(Guid userId, Guid idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        if (!_payments.IsConfigured)
        {
            return Error.BusinessRule("sales.ordering_unavailable", "Cửa hàng chưa mở nhận thanh toán. Vui lòng quay lại sau.");
        }

        Result<IReadOnlyList<CartItemInput>> items = QuoteCartHandler.Validate(new CartQuoteRequest(request.Items));
        if (items.IsFailure)
        {
            return items.Error;
        }

        Result<string?> vat = ValidateExtras(request);
        if (vat.IsFailure)
        {
            return vat.Error;
        }

        UserProfileRef? user = await _users.GetAsync(userId, ct);
        if (user is not { IsActive: true })
        {
            return Error.Forbidden("sales.account_inactive", "Tài khoản không thể đặt hàng.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Một người dùng một luồng tạo đơn tại một thời điểm: giới hạn đơn chờ và idempotency không bị vượt do đua.
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 13))", ct);

        Order? existing = await _db.Set<Order>().AsNoTracking()
            .SingleOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            await tx.RollbackAsync(ct);
            return new CreateOrderResult(await OrderViews.LoadAsync(_db, _keys, _payments, existing.Id, ct), false);
        }

        DateTimeOffset now = _time.GetUtcNow();
        int pending = await _db.Set<Order>().CountAsync(o => o.UserId == userId && o.Status == OrderStatus.PendingPayment, ct);
        if (pending >= _options.MaxPendingOrdersPerUser)
        {
            return Error.Conflict("sales.too_many_pending",
                $"Bạn đang có {pending} đơn chờ thanh toán. Hãy thanh toán hoặc huỷ bớt trước khi đặt đơn mới.");
        }

        int today = await _db.Set<Order>().CountAsync(o => o.UserId == userId && o.CreatedAt > now.AddDays(-1), ct);
        if (today >= _options.MaxOrdersPerUserPerDay)
        {
            return Error.BusinessRule("sales.daily_limit", "Bạn đã đặt quá nhiều đơn trong 24 giờ. Vui lòng liên hệ hỗ trợ.");
        }

        CartQuote quote = await _quote.QuoteAsync(items.Value, ct);
        if (!quote.CanCheckout)
        {
            return Error.Conflict("sales.cart_not_available",
                "Giỏ hàng đã thay đổi (hết hàng hoặc ngừng bán). Vui lòng xem lại giỏ hàng.");
        }

        var order = new Order(userId, idempotencyKey, user.Email, user.FullName, now)
        {
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            VatInfo = vat.Value,
        };
        var reservations = new List<(Guid Variant, Guid Item, int Quantity)>();
        foreach (CartQuoteLine line in quote.Lines)
        {
            ItemDeliveryType delivery = SnakeCaseEnumConverter<ItemDeliveryType>.FromProvider(line.DeliveryType!);
            OrderItem item = order.AddItem(line.VariantId, line.ProductName!, line.VariantName!, line.Sku!, delivery, line.UnitPriceVnd!.Value, line.Quantity);
            if (delivery == ItemDeliveryType.LicenseKey)
            {
                reservations.Add((line.VariantId, item.Id, line.Quantity));
            }
        }

        _db.Add(order);
        _db.Add(new OrderStatusHistory(order.Id, null, OrderStatus.PendingPayment, OrderActorType.Customer, now, userId));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_orders_code" })
        {
            // 40 bit ngẫu nhiên: trùng mã gần như không xảy ra (~10⁻⁸ ở 5.000 đơn). Không thử lại — báo khách bấm lại.
            return Error.Conflict("sales.retry", "Có lỗi tạm thời khi tạo mã đơn. Vui lòng thử lại.");
        }

        foreach ((Guid variant, Guid item, int quantity) in reservations)
        {
            int got = await _keys.ReserveAsync(variant, item, quantity, now, ct);
            if (got < quantity)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("sales.out_of_stock", "Sản phẩm vừa hết hàng. Vui lòng xem lại giỏ hàng.");
            }
        }

        await tx.CommitAsync(ct);
        return new CreateOrderResult(await OrderViews.LoadAsync(_db, _keys, _payments, order.Id, ct), true);
    }

    private static Result<string?> ValidateExtras(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Note is { Length: > 1000 })
        {
            errors["note"] = ["Ghi chú tối đa 1.000 ký tự."];
        }

        string? vatJson = null;
        if (request.VatInfo is { } v)
        {
            if (string.IsNullOrWhiteSpace(v.CompanyName) || v.CompanyName.Length > 200)
            {
                errors["vatInfo.companyName"] = ["Tên công ty 1–200 ký tự."];
            }

            if (v.TaxCode is null || !TaxCodeRegex().IsMatch(v.TaxCode))
            {
                errors["vatInfo.taxCode"] = ["Mã số thuế 10 hoặc 13 số."];
            }

            if (string.IsNullOrWhiteSpace(v.Address) || v.Address.Length > 300)
            {
                errors["vatInfo.address"] = ["Địa chỉ 1–300 ký tự."];
            }

            if (v.Email is { Length: > 254 } || (v.Email is not null && !v.Email.Contains('@')))
            {
                errors["vatInfo.email"] = ["Email nhận hoá đơn không hợp lệ."];
            }

            vatJson = JsonSerializer.Serialize(new { companyName = v.CompanyName?.Trim(), taxCode = v.TaxCode, address = v.Address?.Trim(), email = v.Email?.Trim() });
        }

        return errors.Count > 0 ? Error.Validation("sales.order_invalid", "Thông tin đơn hàng không hợp lệ.", errors) : vatJson;
    }

    [GeneratedRegex("^[0-9]{10}(-?[0-9]{3})?$")]
    private static partial Regex TaxCodeRegex();
}

/// <summary>Xem một đơn của chính mình. Đơn của người khác ⇒ 404 (không lộ là đơn có tồn tại).</summary>
public sealed class GetOrderHandler
{
    private readonly AppDbContext _db;
    private readonly IKeyReservation _keys;
    private readonly IPaymentInstructions _payments;

    internal GetOrderHandler(AppDbContext db, IKeyReservation keys, IPaymentInstructions payments)
    {
        _db = db;
        _keys = keys;
        _payments = payments;
    }

    public async Task<Result<OrderView>> HandleAsync(Guid userId, string code, CancellationToken ct)
    {
        string normalized = code.Trim().ToUpperInvariant();
        Guid? id = OrderCode.IsValid(normalized)
            ? await _db.Set<Order>().Where(o => o.Code == normalized && o.UserId == userId).Select(o => (Guid?)o.Id).SingleOrDefaultAsync(ct)
            : null;
        return id is null
            ? Error.NotFound("sales.order_not_found", "Không tìm thấy đơn hàng.")
            : await OrderViews.LoadAsync(_db, _keys, _payments, id.Value, ct);
    }
}

/// <summary>Khách tự huỷ đơn đang chờ thanh toán: key về kho ngay.</summary>
public sealed class CancelOrderHandler
{
    private readonly AppDbContext _db;
    private readonly IKeyReservation _keys;
    private readonly IPaymentInstructions _payments;
    private readonly TimeProvider _time;

    internal CancelOrderHandler(AppDbContext db, IKeyReservation keys, IPaymentInstructions payments, TimeProvider time)
    {
        _db = db;
        _keys = keys;
        _payments = payments;
        _time = time;
    }

    public async Task<Result<OrderView>> HandleAsync(Guid userId, string code, CancellationToken ct)
    {
        string normalized = code.Trim().ToUpperInvariant();
        Order? order = OrderCode.IsValid(normalized)
            ? await _db.Set<Order>().Include(o => o.Items).SingleOrDefaultAsync(o => o.Code == normalized && o.UserId == userId, ct)
            : null;
        if (order is null)
        {
            return Error.NotFound("sales.order_not_found", "Không tìm thấy đơn hàng.");
        }

        if (order.Status != OrderStatus.PendingPayment)
        {
            return Error.Conflict("sales.not_cancellable", "Chỉ huỷ được đơn đang chờ thanh toán.");
        }

        DateTimeOffset now = _time.GetUtcNow();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        order.Cancel(OrderCancelReason.Customer, now);
        _db.Add(new OrderStatusHistory(order.Id, OrderStatus.PendingPayment, OrderStatus.Cancelled, OrderActorType.Customer, now, userId, "customer_request"));
        try
        {
            // xmin: webhook vừa ghi nhận tiền / job vừa huỷ ⇒ người sau thua, không ghi đè.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("sales.order_changed", "Đơn vừa thay đổi trạng thái. Vui lòng tải lại.");
        }

        await _keys.ReleaseAsync(order.Items.Select(i => i.Id).ToArray(), ct);
        await tx.CommitAsync(ct);
        return await OrderViews.LoadAsync(_db, _keys, _payments, order.Id, ct);
    }
}

/// <summary>
/// "Đơn của tôi": mới nhất trước, phân trang keyset theo <c>created_at</c> (index <c>ix_orders_user_created</c>).
/// Hai đơn của cùng một người không thể cùng micro-giây (tạo đơn tuần tự nhờ advisory lock) ⇒ không cần cột phụ.
/// </summary>
public sealed class ListMyOrdersHandler
{
    public const int PageSize = 20;
    private readonly AppDbContext _db;

    internal ListMyOrdersHandler(AppDbContext db) => _db = db;

    public async Task<Result<OrderPage>> HandleAsync(Guid userId, string? cursor, CancellationToken ct)
    {
        IQueryable<Order> q = _db.Set<Order>().AsNoTracking().Where(o => o.UserId == userId);
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryDecode(cursor, out DateTimeOffset at))
            {
                return Error.Validation("sales.bad_cursor", "Tham số cursor không hợp lệ.", new Dictionary<string, string[]> { ["cursor"] = ["Không hợp lệ."] });
            }

            q = q.Where(o => o.CreatedAt < at);
        }

        var rows = await q.OrderByDescending(o => o.CreatedAt).Take(PageSize + 1)
            .Select(o => new { o.Code, o.Status, o.TotalVnd, ItemCount = o.Items.Count, o.CreatedAt, o.ExpiresAt })
            .ToListAsync(ct);

        string? next = rows.Count > PageSize ? Encode(rows[PageSize - 1].CreatedAt) : null;
        return new OrderPage(
            rows.Take(PageSize).Select(r => new OrderSummary(r.Code, SnakeCaseEnumConverter<OrderStatus>.ToProvider(r.Status), r.TotalVnd, r.ItemCount, r.CreatedAt, r.ExpiresAt)).ToList(),
            next);
    }

    private static string Encode(DateTimeOffset at)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(at.UtcTicks.ToString(CultureInfo.InvariantCulture)));

    private static bool TryDecode(string cursor, out DateTimeOffset at)
    {
        at = default;
        try
        {
            if (!long.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Dựng <see cref="OrderView"/> — key rõ chỉ khi đơn đã giao; hướng dẫn chuyển khoản chỉ khi đang chờ thanh toán.</summary>
internal static class OrderViews
{
    public static async Task<OrderView> LoadAsync(AppDbContext db, IKeyReservation keys, IPaymentInstructions payments, Guid orderId, CancellationToken ct)
    {
        Order order = await db.Set<Order>().AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == orderId, ct);
        List<OrderStatusHistory> history = await db.Set<OrderStatusHistory>().AsNoTracking()
            .Where(h => h.OrderId == orderId).OrderBy(h => h.Id).ToListAsync(ct);

        ILookup<Guid, string> delivered = order.Status is OrderStatus.Fulfilled or OrderStatus.Paid
            ? (await keys.GetDeliveredKeysAsync(order.Items.Select(i => i.Id).ToArray(), ct)).ToLookup(k => k.OrderItemId, k => k.Key)
            : Enumerable.Empty<DeliveredKey>().ToLookup(k => k.OrderItemId, k => k.Key);

        static string S<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToProvider(value);

        return new OrderView(
            order.Code, S(order.Status), order.SubtotalVnd, order.DiscountVnd, order.TotalVnd,
            order.CreatedAt, order.ExpiresAt, order.PaidAt, order.FulfilledAt, order.CancelledAt,
            order.CancelReason is { } r ? S(r) : null, order.Note,
            order.Items.OrderBy(i => i.Id).Select(i => new OrderLineView(
                i.Id, i.VariantId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.SkuSnapshot, S(i.DeliveryType),
                i.UnitPriceVnd, i.Quantity, i.LineTotalVnd, delivered[i.Id].ToList())).ToList(),
            history.Select(h => new OrderEventView(h.FromStatus is { } f ? S(f) : null, S(h.ToStatus), S(h.ActorType), h.Reason, h.CreatedAt)).ToList(),
            order.Status == OrderStatus.PendingPayment ? payments.For(order.Code, order.TotalVnd) : null);
    }
}
