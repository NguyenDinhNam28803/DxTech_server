using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Content.Domain;

/// <summary>
/// Chủ đề theo loại nội dung, dạng cây (ví dụ Giải pháp → "Dịch vụ", Tài nguyên → "Tài liệu" → "Sách").
/// Slug duy nhất trong cùng cha + cùng loại ⇒ URL <c>/giai-phap/dich-vu</c>, <c>/tai-nguyen/tai-lieu/sach</c>.
/// </summary>
internal sealed class Topic : IAuditable, ISoftDeletable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private Topic()
    {
        Slug = null!;
        Name = null!;
    }

    public Topic(PostType postType, string slug, string name, Guid? parentId = null, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        PostType = postType;
        Slug = slug;
        Name = name;
        ParentId = parentId;
    }

    public Guid Id { get; private set; }

    public PostType PostType { get; private set; }

    public Guid? ParentId { get; set; }

    public string Slug { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Ảnh minh hoạ ngoài (chỉ dữ liệu seed, trước khi nhập vào <c>media</c> ở CT-3).</summary>
    public string? ImageUrl { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
