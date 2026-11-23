using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Features.Products;

/// <summary>Quy tắc "sản phẩm đang bán" dùng chung cho danh sách, tìm kiếm và trang chi tiết.</summary>
internal static class ProductListing
{
    /// <summary>
    /// Sản phẩm hiển thị cho khách: đang bật, danh mục đang bật, có ít nhất một gói đang bán.
    /// Bản ghi đã xoá mềm bị loại sẵn bởi query filter (ADR-007).
    /// </summary>
    public static IQueryable<Product> Listed(AppDbContext db)
        => db.Set<Product>()
            .AsNoTracking()
            .Where(p => p.IsActive
                        && p.Category!.IsActive
                        && p.Variants.Any(v => v.IsActive));

    /// <summary>
    /// Id của danh mục <paramref name="slug"/> và mọi danh mục con cháu đang bật (CTE đệ quy — ADR-008).
    /// Danh sách rỗng ⇔ không có danh mục đó (hoặc đang ẩn).
    /// </summary>
    public static Task<List<Guid>> CategoryWithDescendantsAsync(AppDbContext db, string slug, CancellationToken ct)
        => db.Database.SqlQuery<Guid>($"""
            WITH RECURSIVE tree (id, depth) AS (
                SELECT id, 1 FROM catalog.categories
                 WHERE slug = {slug} AND is_active AND deleted_at IS NULL
                UNION ALL
                SELECT c.id, t.depth + 1 FROM catalog.categories c
                  JOIN tree t ON c.parent_id = t.id
                 WHERE c.is_active AND c.deleted_at IS NULL AND t.depth < {Category.MaxDepth}
            )
            SELECT id AS "Value" FROM tree
            """).ToListAsync(ct);
}
