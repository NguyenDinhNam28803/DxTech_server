using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;

namespace server.Modules.Identity.Jobs;

/// <summary>
/// Dọn refresh token hết hạn hoặc đã thu hồi quá 30 ngày (ADR-012, thiết kế DB mục 13.2), mỗi ngày một lần,
/// theo lô 1.000 dòng để không khoá bảng lâu. Lần đầu chạy sau 10 phút kể từ khi API khởi động.
/// </summary>
internal sealed class RefreshTokenCleanup(IServiceScopeFactory scopes, TimeProvider time, ILogger<RefreshTokenCleanup> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
            do
            {
                try
                {
                    int deleted = await RunOnceAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        logger.LogInformation("Refresh token cleanup: deleted {Count} row(s)", deleted);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Refresh token cleanup failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // API đang dừng.
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = time.GetUtcNow() - Retention;
        int total = 0;
        while (true)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int n = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM identity.refresh_tokens
                 WHERE id IN (SELECT id FROM identity.refresh_tokens
                               WHERE expires_at < {cutoff} OR revoked_at < {cutoff}
                               LIMIT {BatchSize})
                """, ct);
            total += n;
            if (n < BatchSize)
            {
                return total;
            }
        }
    }
}
