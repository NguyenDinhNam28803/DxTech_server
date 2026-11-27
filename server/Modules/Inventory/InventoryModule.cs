namespace server.Modules.Inventory;

/// <summary>
/// Module Inventory: kho license key (mã hoá AES-256-GCM — ADR-014) và lô nhập — schema PostgreSQL <c>inventory</c>.
/// PR-9 chỉ có Domain + Persistence; mã hoá, CLI nhập key ở PR-11.
/// </summary>
public static class InventoryModule
{
    public const string Schema = "inventory";

    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddScoped<Contracts.IStockQuery, Features.StockQuery>();
        return services;
    }
}
