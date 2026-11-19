namespace server.Modules.Catalog;

/// <summary>
/// Module Catalog: danh mục, sản phẩm, gói (variant) — schema PostgreSQL <c>catalog</c> (ADR-001, ADR-002).
///
/// Bố cục bên trong module:
///   Contracts/    — interface + DTO công khai cho module khác (ICatalogApi). Chỉ thư mục này là public.
///   Domain/       — entity và quy tắc nghiệp vụ (internal).
///   Persistence/  — IEntityTypeConfiguration&lt;&gt;, ToTable(..., schema: "catalog").
///   Features/     — mỗi file một use case: Query/Command + Handler + Response.
///   Api/          — controller mỏng, chỉ gọi handler và đổi Result sang HTTP.
///
/// Bảng và use case được thêm ở PR-3 (migration Catalog_Initial) và PR-4 (Catalog API).
/// </summary>
public static class CatalogModule
{
    public const string Schema = "catalog";

    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Đăng ký handler của module tại đây khi có use case, ví dụ:
        // services.AddScoped<ListProductsHandler>();
        _ = configuration;
        return services;
    }
}
