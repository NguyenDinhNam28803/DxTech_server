namespace server.Modules.Sales;

/// <summary>
/// Module Sales: đơn hàng, dòng đơn, lịch sử trạng thái — schema PostgreSQL <c>sales</c> (ADR-013,
/// <c>docs/database/phase3-schema.md</c>). PR-9 chỉ có Domain + Persistence; API đặt hàng ở PR-11.
/// </summary>
public static class SalesModule
{
    public const string Schema = "sales";
}
