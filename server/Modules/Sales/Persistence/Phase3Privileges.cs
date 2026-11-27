namespace server.Modules.Sales.Persistence;

/// <summary>
/// Thu hồi quyền thừa của role <c>dxtechs_app</c> trên bảng Phase 3 (thiết kế PR-9 mục A5, ADR-009):
/// đơn, dòng đơn, key, lô nhập — không DELETE; lịch sử trạng thái và payment — chỉ thêm, payment chỉ được sửa
/// 3 cột xử lý khoản lệch. Chạy ở migration (lần tạo bảng) VÀ ở <c>deploy/postgres/roles.sql</c> mục 5b
/// (mỗi lần deploy, vì mục 5 cấp lại SELECT/INSERT/UPDATE/DELETE cho mọi bảng). Giữ hai bản giống hệt nhau —
/// test <c>Phase3_privileges_match_roles_sql</c> so sánh.
/// Bỏ qua khi không có role dxtechs_app (database test, máy dev).
/// </summary>
internal static class Phase3Privileges
{
    public const string Sql = """
        DO $phase3$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_app') THEN
            REVOKE DELETE, TRUNCATE ON sales.orders, sales.order_items, inventory.license_keys, inventory.import_batches FROM dxtechs_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON sales.order_status_history, payments.payments FROM dxtechs_app;
            GRANT UPDATE (resolved_at, resolved_by, resolution_note) ON payments.payments TO dxtechs_app;
            REVOKE TRUNCATE ON payments.webhook_events FROM dxtechs_app;
          END IF;
        END
        $phase3$;
        """;
}
