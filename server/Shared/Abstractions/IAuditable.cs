namespace server.Shared.Abstractions;

/// <summary>
/// Entity có mốc thời gian tạo/cập nhật. Giá trị do <c>AuditableInterceptor</c> điền khi SaveChanges,
/// lấy từ <see cref="TimeProvider"/> (UTC) — code nghiệp vụ không tự gán (ADR-006).
/// Trong PostgreSQL map thành <c>timestamptz</c>.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}
