using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Payments.Features.Sepay;

namespace server.Modules.Payments.Jobs;

/// <summary>
/// Mỗi phút: xử lý lại sự kiện webhook hợp lệ chưa xử lý (API lỗi giữa chừng — ADR-013 §3 bước 4), tối đa 10 lần/sự kiện.
/// Mỗi ngày: xoá sự kiện cũ hơn 180 ngày không gắn với payment nào (payment là chứng từ, giữ lâu dài).
/// </summary>
internal sealed class WebhookMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<WebhookMaintenance> logger) : BackgroundService
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
            do
            {
                try
                {
                    int n = await RetryPendingAsync(stoppingToken);
                    if (n > 0)
                    {
                        logger.LogInformation("Webhook retry: processed {Count} event(s)", n);
                    }

                    if (time.GetUtcNow() - _lastCleanup > TimeSpan.FromDays(1))
                    {
                        _lastCleanup = time.GetUtcNow();
                        await CleanupAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Webhook maintenance failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // API đang dừng.
        }
    }

    /// <summary>Xử lý lại các sự kiện còn treo; trả về số sự kiện xử lý thành công.</summary>
    public async Task<int> RetryPendingAsync(CancellationToken ct)
    {
        List<Guid> pending;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pending = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM payments.webhook_events
                 WHERE processed_at IS NULL AND signature_valid AND attempts < {MaxAttempts}
                 ORDER BY received_at LIMIT 50
                """).ToListAsync(ct);
        }

        int done = 0;
        foreach (Guid id in pending)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            WebhookProcessor processor = scope.ServiceProvider.GetRequiredService<WebhookProcessor>();
            try
            {
                await processor.ProcessAsync(id, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook event {EventId} retry failed", id);
                scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
                await processor.RecordFailureAsync(id, ex, ct);
            }
        }

        return done;
    }

    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset cutoff = time.GetUtcNow() - Retention;
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM payments.webhook_events e
             WHERE e.received_at < {cutoff}
               AND NOT EXISTS (SELECT 1 FROM payments.payments p WHERE p.webhook_event_id = e.id)
            """, ct);
    }
}
