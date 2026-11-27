using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;

namespace server.Modules.Content.Jobs;

/// <summary>
/// Job nền mỗi ngày (thiết kế CT mục 1.3, ADR-015 §6):
/// (1) giữ 50 revision mới nhất mỗi bài — bản cũ hơn bị xoá (role app có DELETE trên post_revisions, không có UPDATE);
/// (2) ẩn danh hoá lead quá 24 tháng — xoá họ tên/liên hệ/nội dung/IP/UA bằng UPDATE (role app không có DELETE trên leads),
///     giữ loại, bài, trạng thái, thời điểm để vẫn đếm được thống kê.
/// </summary>
internal sealed class ContentMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<ContentMaintenance> logger) : BackgroundService
{
    public const int RevisionsKept = 50;
    public const int LeadRetentionMonths = 24;
    public const string AnonymizedName = "Đã ẩn danh";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
            do
            {
                try
                {
                    (int revisions, int leads) = await RunOnceAsync(stoppingToken);
                    if (revisions + leads > 0)
                    {
                        logger.LogInformation("Content maintenance: pruned {Revisions} revision(s), anonymized {Leads} lead(s)", revisions, leads);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Content maintenance failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // API đang dừng.
        }
    }

    public async Task<(int Revisions, int Leads)> RunOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset cutoff = time.GetUtcNow().AddMonths(-LeadRetentionMonths);

        int revisions = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM content.post_revisions r
             USING (SELECT id, row_number() OVER (PARTITION BY post_id ORDER BY id DESC) AS n FROM content.post_revisions) ranked
             WHERE r.id = ranked.id AND ranked.n > {RevisionsKept}
            """, ct);

        int leads = await db.Database.ExecuteSqlAsync($"""
            UPDATE content.leads
               SET full_name = {AnonymizedName}, email = NULL, phone = NULL, company = NULL, message = NULL,
                   note = NULL, source_ip = NULL, user_agent = NULL, anonymized_at = now(), updated_at = now()
             WHERE anonymized_at IS NULL AND created_at < {cutoff}
            """, ct);
        return (revisions, leads);
    }
}
