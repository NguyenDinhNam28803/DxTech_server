using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Catalog.Domain;

/// <summary>
/// Sản phẩm (ví dụ "Microsoft 365 Family"). Giá nằm ở <see cref="ProductVariant"/>: một sản phẩm có nhiều gói
/// (thời hạn, số người dùng). Bảng <c>catalog.products</c>; cột <c>search_vector</c> do PostgreSQL tự tính
/// (generated column) nên không có trong class — truy vấn qua <c>EF.Property</c> ở tầng Features.
/// </summary>
internal sealed class Product : IAuditable, ISoftDeletable
{
    public const int NameMaxLength = 200;

    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private Product()
    {
        Slug = null!;
        Name = null!;
    }

    public Product(Guid categoryId, string slug, string name, LicenseType licenseType, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        CategoryId = categoryId;
        Slug = slug;
        Name = name;
        LicenseType = licenseType;
    }

    public Guid Id { get; private set; }

    public Guid CategoryId { get; set; }

    public Category? Category { get; private set; }

    public string Slug { get; set; }

    public string Name { get; set; }

    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    /// <summary>Hãng (Microsoft, Adobe…) — dùng để lọc và tìm kiếm.</summary>
    public string? Brand { get; set; }

    public LicenseType LicenseType { get; set; }

    /// <summary>Giá trị trong <see cref="Domain.Platforms.All"/>; cột <c>text[]</c> có index GIN để lọc theo hệ điều hành.</summary>
    public List<string> Platforms { get; set; } = [];

    /// <summary>Danh sách tính năng chỉ để hiển thị — cột <c>jsonb</c>.</summary>
    public List<string> Features { get; set; } = [];

    public string? ImageUrl { get; set; }

    /// <summary>Nhãn nổi bật ("Bán chạy nhất"); null = không có.</summary>
    public string? Badge { get; set; }

    public bool IsActive { get; set; } = true;

    public List<ProductVariant> Variants { get; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
