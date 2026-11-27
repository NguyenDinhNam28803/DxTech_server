using server.Modules.Catalog.Features.Categories;
using server.Modules.Catalog.Features.Products;
using server.Modules.Catalog.Seeding;

namespace server.Modules.Catalog;

/// <summary>
/// Module Catalog: danh mục, sản phẩm, gói (variant) — schema PostgreSQL <c>catalog</c> (ADR-001, ADR-002).
///
/// Bố cục bên trong module:
///   Contracts/    — interface + DTO công khai cho module khác (ICatalogApi). Chỉ thư mục này là public.
///   Domain/       — entity và quy tắc nghiệp vụ (internal).
///   Persistence/  — IEntityTypeConfiguration&lt;&gt;, ToTable(..., schema: "catalog").
///   Features/     — mỗi file một use case: Query/Command + Handler + Response. Public vì controller (bắt buộc
///                   public) nhận/trả các kiểu này; module khác vẫn chỉ được dùng Contracts (test kiến trúc).
///   Api/          — controller mỏng, chỉ gọi handler và đổi Result sang HTTP.
///
/// Bảng: PR-3 (migration Catalog_Initial). Use case + API: PR-4.
/// </summary>
public static class CatalogModule
{
    public const string Schema = "catalog";

    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;

        services.AddScoped<GetCategoryTreeHandler>();
        services.AddScoped<ListProductsHandler>();
        services.AddScoped(sp => new GetProductBySlugHandler(
            sp.GetRequiredService<Infrastructure.Persistence.AppDbContext>(),
            sp.GetRequiredService<Inventory.Contracts.IStockQuery>()));
        services.AddScoped<DemoCatalogSeeder>();
        services.AddScoped<Contracts.IVariantPricing, VariantPricing>();
        services.AddScoped<Contracts.IVariantDirectory, VariantDirectory>();
        services.AddScoped<Contracts.IProductSitemap, ProductSitemap>();
        return services;
    }

    /// <summary>
    /// Lệnh <c>--seed-demo [--dry-run]</c> (ADR-010): nạp dữ liệu demo rồi thoát, không khởi động web server.
    /// Trả mã thoát 0 khi thành công, 1 khi lỗi (để script/CI biết).
    /// </summary>
    public static async Task<int> SeedDemoAsync(IServiceProvider services, bool dryRun, TextWriter output, CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        try
        {
            SeedReport r = await scope.ServiceProvider.GetRequiredService<DemoCatalogSeeder>().RunAsync(dryRun, ct);
            await output.WriteLineAsync(
                $"seed-demo{(r.DryRun ? " (DRY RUN — đã rollback, không ghi gì)" : "")}: " +
                $"danh mục +{r.CategoriesCreated} (bỏ qua {r.CategoriesSkipped}), " +
                $"sản phẩm +{r.ProductsCreated} (bỏ qua {r.ProductsSkipped}), gói +{r.VariantsCreated}");
            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"seed-demo THẤT BẠI — không ghi gì (transaction rollback): {ex.GetBaseException().Message}");
            return 1;
        }
    }
}
