using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using server.Shared.Abstractions;

namespace server.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Điền <c>created_at</c>/<c>updated_at</c> cho mọi entity <see cref="IAuditable"/> ngay trước khi lưu.
/// Thời gian lấy từ <see cref="TimeProvider"/> nên test giả lập được (ADR-006, mục 10 tài liệu thiết kế).
/// Không cho sửa <c>CreatedAt</c> sau khi tạo.
/// </summary>
public sealed class AuditableInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            ApplyTimestamps(eventData.Context.ChangeTracker);
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            ApplyTimestamps(eventData.Context.ChangeTracker);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    internal void ApplyTimestamps(ChangeTracker changeTracker)
    {
        // Một mốc thời gian cho cả lần SaveChanges: mọi dòng trong cùng giao dịch có cùng giờ.
        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (EntityEntry<IAuditable> entry in changeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
