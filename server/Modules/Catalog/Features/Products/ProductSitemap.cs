using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Contracts;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Features.Products;

/// <summary>Sản phẩm đang bán (chưa xoá, đang bật, có ít nhất một gói đang bán) — cùng điều kiện với trang danh sách.</summary>
internal sealed class ProductSitemap(AppDbContext db) : IProductSitemap
{
    public async Task<IReadOnlyList<ProductSitemapEntry>> ListAsync(CancellationToken ct)
        => await db.Set<Product>().AsNoTracking()
            .Where(p => p.IsActive && p.Variants.Any(v => v.IsActive))
            .OrderBy(p => p.Slug)
            .Select(p => new ProductSitemapEntry(p.Slug, p.UpdatedAt))
            .ToListAsync(ct);
}
