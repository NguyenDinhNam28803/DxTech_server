using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Content.Domain;

/// <summary>
/// Mọi nội dung hiển thị trên web (ADR-015 §2): tin tức, trang giải pháp, khoá đào tạo, tài nguyên, thuật ngữ, trang tĩnh.
/// Thân bài là mảng khối JSON có cấu trúc (<see cref="Blocks"/>, kiểm bằng <see cref="ContentBlocks"/>) — không có HTML.
/// <para>
/// <see cref="ParentId"/>: bài con của một bài (trang hạng mục <c>/giai-phap/dich-vu/phat-trien-phan-mem/&lt;hạng-mục&gt;</c>
/// nằm dưới trang <c>phat-trien-phan-mem</c>). Bổ sung so với thiết kế CT để giữ đúng URL 3 cấp đang chạy.
/// </para>
/// </summary>
internal sealed class Post : IAuditable, ISoftDeletable
{
    public const int TitleMaxLength = 200;
    public const int ExcerptMaxLength = 500;
    public const int LabelMaxLength = 40;
    public const int SeoTitleMaxLength = 70;
    public const int SeoDescriptionMaxLength = 160;
    public const int MaxKeyPoints = 10;
    public const int KeyPointMaxLength = 200;

    private Post()
    {
        Slug = null!;
        Title = null!;
        Blocks = "[]";
    }

    public Post(PostType postType, string slug, string title, Guid authorId, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        PostType = postType;
        Slug = slug;
        Title = title;
        AuthorId = authorId;
        UpdatedBy = authorId;
        Blocks = "[]";
    }

    public Guid Id { get; private set; }

    public PostType PostType { get; private set; }

    public Guid? TopicId { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>Cố định sau khi tạo — đổi slug phải qua thao tác riêng (ghi <c>slug_redirects</c>), không sinh lại từ tiêu đề.</summary>
    public string Slug { get; set; }

    public string Title { get; set; }

    public string? Excerpt { get; set; }

    /// <summary>Nhãn thẻ tin ("HƯỚNG DẪN", "BẢO MẬT"…).</summary>
    public string? Label { get; set; }

    /// <summary>JSON mảng khối (cột <c>jsonb</c>). Chỉ ghi chuỗi đã qua <see cref="ContentBlocks.Validate"/>.</summary>
    public string Blocks { get; set; }

    public List<string> KeyPoints { get; set; } = [];

    public Guid? CoverMediaId { get; set; }

    /// <summary>Ảnh bìa ngoài từ dữ liệu cũ — CT-3 tải về thành <see cref="CoverMediaId"/> rồi xoá giá trị này.</summary>
    public string? CoverImageUrl { get; set; }

    public string? CoverImageAlt { get; set; }

    public PostStatus Status { get; set; } = PostStatus.Draft;

    public DateTimeOffset? PublishedAt { get; set; }

    public Guid AuthorId { get; private set; }

    public Guid UpdatedBy { get; set; }

    public string? SeoTitle { get; set; }

    public string? SeoDescription { get; set; }

    public short? ReadingMinutes { get; set; }

    public int SortOrder { get; set; }

    public bool IsFeatured { get; set; }

    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Đặt trạng thái đã đăng; <paramref name="at"/> ở tương lai = hẹn giờ.</summary>
    public void Publish(DateTimeOffset at)
    {
        Status = PostStatus.Published;
        PublishedAt = at;
    }
}
