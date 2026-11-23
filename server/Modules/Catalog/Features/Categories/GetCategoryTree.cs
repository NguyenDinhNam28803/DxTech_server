using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Shared.Results;

namespace server.Modules.Catalog.Features.Categories;

/// <summary>Một nút trong cây danh mục (<c>GET /api/categories</c>).</summary>
public sealed record CategoryNode(Guid Id, string Slug, string Name, string? Description, IReadOnlyList<CategoryNode> Children);

/// <summary>
/// Cây danh mục đang hiển thị. Cây nhỏ (&lt; 50 nút) nên đọc một lần rồi dựng cây trong bộ nhớ — một câu SQL,
/// không N+1 (ADR-008: adjacency list). Danh mục ẩn/đã xoá bị bỏ cùng toàn bộ nhánh con của nó.
/// </summary>
public sealed class GetCategoryTreeHandler(AppDbContext db)
{
    public async Task<Result<IReadOnlyList<CategoryNode>>> HandleAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Set<Category>()
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.ParentId, c.Slug, c.Name, c.Description })
            .ToListAsync(cancellationToken);

        ILookup<Guid?, (Guid Id, string Slug, string Name, string? Description)> byParent =
            rows.ToLookup(r => r.ParentId, r => (r.Id, r.Slug, r.Name, r.Description));

        IReadOnlyList<CategoryNode> Build(Guid? parentId, int depth)
            => depth > Category.MaxDepth
                ? []   // chặn dữ liệu hỏng (vòng lặp cha–con) thay vì đệ quy vô hạn
                : byParent[parentId]
                    .Select(r => new CategoryNode(r.Id, r.Slug, r.Name, r.Description, Build(r.Id, depth + 1)))
                    .ToList();

        return Result.Success(Build(null, 1));
    }
}
