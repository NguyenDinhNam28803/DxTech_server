using System.ComponentModel.DataAnnotations;

namespace server.Infrastructure.Options;

/// <summary>
/// Cấu hình kết nối database, đọc từ section <c>ConnectionStrings</c>
/// (biến môi trường <c>ConnectionStrings__DefaultConnection</c> trên VPS).
/// Được kiểm tra khi khởi động (ValidateOnStart): thiếu chuỗi kết nối thì API không lên,
/// health check của CI báo đỏ và API cũ vẫn tiếp tục phục vụ.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required(ErrorMessage = "Thiếu ConnectionStrings:DefaultConnection.")]
    public string DefaultConnection { get; set; } = string.Empty;
}
