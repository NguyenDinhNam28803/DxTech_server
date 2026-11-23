using server.Modules.Catalog.Features.Categories;
using server.Modules.Catalog.Features.Products;

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
        services.AddScoped<GetProductBySlugHandler>();
        return services;
    }
}
