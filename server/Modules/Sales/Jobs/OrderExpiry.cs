using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Inventory.Contracts;

namespace server.Modules.Sales.Jobs;

/// <summary>
/// Huỷ đơn chờ thanh toán quá hạn 30 phút (ADR-013 §5) và trả key về kho — chạy mỗi phút, theo lô 100 đơn.
/// <c>FOR UPDATE SKIP LOCKED</c>: không chờ đơn đang được webhook / khách xử lý; lần chạy sau sẽ lấy lại nếu vẫn quá hạn.
/// Webhook đến sau khi đơn đã huỷ ⇒ nhánh "tiền đến muộn" (PR-12).
/// </summary>
internal sealed class OrderExpiry(IServiceScopeFactory scopes, TimeProvider time, ILogger<OrderExpiry> logger) : BackgroundService
{
    public const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
            do
            {
                try
                {
                    int n = await RunOnceAsync(stoppingToken);
                    if (n > 0)
                    {
                        logger.LogInformation("Order expiry: cancelled {Count} order(s)", n);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Order expiry failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // API đang dừng.
        }
    }

    /// <summary>Chạy hết các lô quá hạn; trả về số đơn đã huỷ.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IKeyReservation keys = scope.ServiceProvider.GetRequiredService<IKeyReservation>();
            DateTimeOffset now = time.GetUtcNow();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            List<Guid> ids = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM sales.orders
                 WHERE status = 'pending_payment' AND expires_at <= {now}
                 ORDER BY expires_at
                 LIMIT {BatchSize}
                 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (ids.Count == 0)
            {
                return total;
            }

            Guid[] idArray = ids.ToArray();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE sales.orders
                   SET status = 'cancelled', cancelled_at = {now}, cancel_reason = 'expired', updated_at = {now}
                 WHERE id = ANY({idArray})
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO sales.order_status_history (order_id, from_status, to_status, actor_type, reason, created_at)
                SELECT id, 'pending_payment', 'cancelled', 'system', 'expired', {now} FROM unnest({idArray}) AS t(id)
                """, ct);
            List<Guid> items = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM sales.order_items WHERE order_id = ANY({idArray})
                """).ToListAsync(ct);
            await keys.ReleaseAsync(items, ct);
            await tx.CommitAsync(ct);

            total += ids.Count;
            if (ids.Count < BatchSize)
            {
                return total;
            }
        }
    }
}
