namespace server.Modules.Sales;

/// <summary>
/// Module Sales: đơn hàng, dòng đơn, lịch sử trạng thái — schema PostgreSQL <c>sales</c> (ADR-013,
/// <c>docs/database/phase3-schema.md</c>). PR-9: Domain + Persistence. PR-10: báo giá giỏ hàng. PR-11: đặt hàng.
/// </summary>
public static class SalesModule
{
    public const string Schema = "sales";

    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        // Constructor internal (nhận interface của module khác) ⇒ đăng ký tường minh như Identity.
        services.AddScoped(sp => new Features.Cart.QuoteCartHandler(
            sp.GetRequiredService<Catalog.Contracts.IVariantPricing>(),
            sp.GetRequiredService<Inventory.Contracts.IStockQuery>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
