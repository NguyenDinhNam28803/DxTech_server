namespace server.Modules.Identity.Contracts;

/// <summary>Tên policy phân quyền cho controller của module khác (ADR-012 §5).</summary>
public static class Policies
{
    /// <summary>Admin đang hoạt động — kiểm tra lại trạng thái trong DB.</summary>
    public const string Admin = "admin";
}
