using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;
using server.Shared.Results;

namespace server.Modules.Catalog.Features.Products;

public sealed record ProductVariantItem(
    Guid Id,
    string Sku,
    string Name,
    int? TermMonths,
    int Seats,
    long PriceVnd,
    long? CompareAtPriceVnd,
    string DeliveryType,
    bool IsDefault);

/// <summary>Trang chi tiết sản phẩm (<c>GET /api/products/{slug}</c>). Breadcrumb đi từ danh mục gốc tới danh mục của sản phẩm.</summary>
public sealed record ProductDetail(
    Guid Id,
    string Slug,
    string Name,
    string? ShortDescription,
    string? Description,
    string? Brand,
    string LicenseType,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> Features,
    string? ImageUrl,
    string? Badge,
    IReadOnlyList<CategoryRef> Breadcrumb,
    IReadOnlyList<ProductVariantItem> Variants);

public sealed class GetProductBySlugHandler(AppDbContext db)
{
    public async Task<Result<ProductDetail>> HandleAsync(string slug, CancellationToken ct)
    {
        var product = await ProductListing.Listed(db)
            .Where(p => p.Slug == slug)
            .Select(p => new
            {
                p.Id, p.Slug, p.Name, p.ShortDescription, p.Description, p.Brand, p.LicenseType,
                p.Platforms, p.Features, p.ImageUrl, p.Badge, p.CategoryId,
                Variants = p.Variants.Where(v => v.IsActive)
                    .OrderBy(v => v.SortOrder).ThenBy(v => v.PriceVnd)
                    .Select(v => new { v.Id, v.Sku, v.Name, v.TermMonths, v.Seats, v.PriceVnd, v.CompareAtPriceVnd, v.DeliveryType, v.IsDefault })
                    .ToList(),
            })
            .AsSplitQuery()
            .SingleOrDefaultAsync(ct);

        if (product is null)
        {
            // Không phân biệt "không tồn tại" với "đang ẩn/đã xoá": không lộ sản phẩm chưa công bố.
            return Error.NotFound("catalog.product_not_found", $"Không tìm thấy sản phẩm '{slug}'.");
        }

        List<CategoryRef> breadcrumb = await db.Database.SqlQuery<BreadcrumbRow>($"""
            WITH RECURSIVE up (id, parent_id, slug, name, depth) AS (
                SELECT id, parent_id, slug, name, 1 FROM catalog.categories WHERE id = {product.CategoryId}
                UNION ALL
                SELECT c.id, c.parent_id, c.slug, c.name, u.depth + 1 FROM catalog.categories c
                  JOIN up u ON c.id = u.parent_id
                 WHERE u.depth < {Category.MaxDepth}
            )
            SELECT slug AS "Slug", name AS "Name", depth AS "Depth" FROM up
            """)
            .OrderByDescending(r => r.Depth)
            .Select(r => new CategoryRef(r.Slug, r.Name))
            .ToListAsync(ct);

        return new ProductDetail(
            product.Id, product.Slug, product.Name, product.ShortDescription, product.Description, product.Brand,
            SnakeCaseEnumConverter<LicenseType>.ToProvider(product.LicenseType),
            product.Platforms, product.Features, product.ImageUrl, product.Badge,
            breadcrumb,
            product.Variants.Select(v => new ProductVariantItem(
                    v.Id, v.Sku, v.Name, v.TermMonths, v.Seats, v.PriceVnd, v.CompareAtPriceVnd,
                    SnakeCaseEnumConverter<DeliveryType>.ToProvider(v.DeliveryType), v.IsDefault))
                .ToList());
    }

    internal sealed class BreadcrumbRow
    {
        public string Slug { get; set; } = "";

        public string Name { get; set; } = "";

        public int Depth { get; set; }
    }
}
