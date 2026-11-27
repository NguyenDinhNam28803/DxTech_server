using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Contracts;
using server.Modules.Identity.Contracts;
using server.Modules.Inventory.Contracts;
using server.Modules.Payments.Contracts;
using server.Modules.Sales.Features.Cart;
using server.Modules.Sales.Features.Orders;
using server.Modules.Sales.Jobs;

namespace server.Modules.Sales;

/// <summary>
/// Module Sales: giỏ hàng, đơn hàng, lịch sử trạng thái — schema PostgreSQL <c>sales</c> (ADR-013,
/// <c>docs/database/phase3-schema.md</c>). PR-9: bảng. PR-10: báo giá giỏ hàng. PR-11: đặt/xem/huỷ đơn, job hết hạn.
/// Module khác chỉ được dùng qua Contracts (Catalog, Inventory, Payments, Identity).
/// </summary>
public static class SalesModule
{
    public const string Schema = "sales";

    public static IServiceCollection AddSalesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SalesOptions>().Bind(configuration.GetSection(SalesOptions.SectionName));

        // Constructor internal (nhận interface của module khác) ⇒ đăng ký tường minh như Identity.
        services.AddScoped(sp => new QuoteCartHandler(
            sp.GetRequiredService<IVariantPricing>(), sp.GetRequiredService<IStockQuery>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new CreateOrderHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<QuoteCartHandler>(), sp.GetRequiredService<IKeyReservation>(),
            sp.GetRequiredService<IUserProfiles>(), sp.GetRequiredService<IPaymentInstructions>(),
            sp.GetRequiredService<IOptions<SalesOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new GetOrderHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IKeyReservation>(), sp.GetRequiredService<IPaymentInstructions>()));
        services.AddScoped(sp => new CancelOrderHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IKeyReservation>(), sp.GetRequiredService<IPaymentInstructions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new ListMyOrdersHandler(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<Contracts.IOrderPayments>(sp => new OrderPayments(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IKeyReservation>(), sp.GetRequiredService<ILogger<OrderPayments>>()));

        services.AddSingleton<OrderExpiry>();
        services.AddHostedService(sp => sp.GetRequiredService<OrderExpiry>());
        return services;
    }
}
