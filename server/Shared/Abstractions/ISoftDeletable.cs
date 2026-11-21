namespace server.Shared.Abstractions;

/// <summary>
/// Entity xoá mềm (quyết định D8: chỉ dữ liệu catalog). Xoá = đặt <see cref="DeletedAt"/>; dòng vẫn còn để
/// đơn hàng cũ tham chiếu được. Cấu hình EF của entity phải có query filter <c>DeletedAt == null</c> và
/// mọi unique index nghiệp vụ phải có <c>WHERE deleted_at IS NULL</c>.
/// </summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}
