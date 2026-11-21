using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Catalog.Domain;

/// <summary>
/// Danh mục sản phẩm, dạng cây cha–con (adjacency list, ADR-008), sâu tối đa 3 cấp.
/// Bảng <c>catalog.categories</c>.
/// </summary>
internal sealed class Category : IAuditable, ISoftDeletable
{
    public const int MaxDepth = 3;
    public const int NameMaxLength = 100;

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private Category()
    {
        Slug = null!;
        Name = null!;
    }

    public Category(string slug, string name, Guid? parentId = null, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        Slug = slug;
        Name = name;
        ParentId = parentId;
    }

    public Guid Id { get; private set; }

    /// <summary>null = danh mục gốc.</summary>
    public Guid? ParentId { get; set; }

    public Category? Parent { get; private set; }

    public List<Category> Children { get; } = [];

    /// <summary>Chỉ a-z, 0-9, dấu gạch ngang; duy nhất trong các danh mục chưa xoá.</summary>
    public string Slug { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
