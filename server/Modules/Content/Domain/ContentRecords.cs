using System.Net;
using server.Shared.Abstractions;
using server.Shared.Ids;

namespace server.Modules.Content.Domain;

/// <summary>Ảnh chụp một lần lưu bài — xem lại/khôi phục. Chỉ thêm (DB không cho UPDATE); job dọn giữ 50 bản/bài.</summary>
internal sealed class PostRevision
{
    private PostRevision()
    {
        Title = null!;
        Blocks = null!;
    }

    public PostRevision(Post post, Guid savedBy)
    {
        PostId = post.Id;
        Title = post.Title;
        Blocks = post.Blocks;
        KeyPoints = [.. post.KeyPoints];
        SavedBy = savedBy;
    }

    public long Id { get; private set; }

    public Guid PostId { get; private set; }

    public string Title { get; private set; }

    public string Blocks { get; private set; }

    public List<string> KeyPoints { get; private set; } = [];

    public Guid SavedBy { get; private set; }

    public DateTimeOffset SavedAt { get; private set; }
}

/// <summary>Slug cũ của bài ⇒ API chi tiết trả 301 sang slug mới (ADR-015 §2).</summary>
internal sealed class SlugRedirect
{
    private SlugRedirect() => OldSlug = null!;

    public SlugRedirect(PostType postType, string oldSlug, Guid postId)
    {
        PostType = postType;
        OldSlug = oldSlug;
        PostId = postId;
    }

    public long Id { get; private set; }

    public PostType PostType { get; private set; }

    public string OldSlug { get; private set; }

    public Guid PostId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Metadata ảnh tải lên (file nằm trên đĩa VPS, ADR-015 §5). Xoá mềm; <c>sha256</c> UNIQUE chống tải trùng.</summary>
internal sealed class MediaFile : ISoftDeletable
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 6000;
    public static readonly string[] ContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private MediaFile()
    {
        FileName = null!;
        ContentType = null!;
        Sha256 = null!;
    }

    public MediaFile(string contentType, string extension, long bytes, int width, int height, byte[] sha256, Guid uploadedBy, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        FileName = $"{Id:N}.{extension}";
        ContentType = contentType;
        Bytes = bytes;
        Width = width;
        Height = height;
        Sha256 = sha256;
        UploadedBy = uploadedBy;
    }

    public Guid Id { get; private set; }

    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    public long Bytes { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string? AltDefault { get; set; }

    public byte[] Sha256 { get; private set; }

    public Guid UploadedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Lịch khai giảng của một bài <c>training</c>.</summary>
internal sealed class CourseSession : IAuditable
{
    private CourseSession() { }

    public CourseSession(Guid postId, DateOnly startsOn, CourseFormat format, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        PostId = postId;
        StartsOn = startsOn;
        Format = format;
    }

    public Guid Id { get; private set; }

    public Guid PostId { get; private set; }

    public DateOnly StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public CourseFormat Format { get; set; }

    public string? Location { get; set; }

    public int? Seats { get; set; }

    /// <summary>Học phí hiển thị công khai; null = "liên hệ" (câu hỏi mở 3 của thiết kế CT).</summary>
    public long? PriceVnd { get; set; }

    public CourseSessionStatus Status { get; set; } = CourseSessionStatus.Open;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Đăng ký tư vấn/khoá học từ form công khai (ADR-015 §6). Có đồng ý xử lý dữ liệu (NĐ 13/2023): phiên bản văn bản + thời điểm.
/// Không xoá (DB không cho DELETE) — sau 24 tháng ẩn danh hoá bằng UPDATE, ghi <see cref="AnonymizedAt"/>.
/// </summary>
internal sealed class Lead : IAuditable
{
    public const int MessageMaxLength = 2000;

    private Lead()
    {
        FullName = null!;
        ConsentVersion = null!;
    }

    public Lead(LeadKind kind, string fullName, string consentVersion, DateTimeOffset consentAt, Guid? id = null)
    {
        Id = id ?? UuidV7.NewGuid();
        Kind = kind;
        FullName = fullName;
        ConsentVersion = consentVersion;
        ConsentAt = consentAt;
    }

    public Guid Id { get; private set; }

    public LeadKind Kind { get; private set; }

    public Guid? PostId { get; set; }

    public Guid? SessionId { get; set; }

    public string FullName { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Company { get; set; }

    public string? Message { get; set; }

    public string ConsentVersion { get; private set; }

    public DateTimeOffset ConsentAt { get; private set; }

    public IPAddress? SourceIp { get; set; }

    public string? UserAgent { get; set; }

    public LeadStatus Status { get; set; } = LeadStatus.New;

    public Guid? HandledBy { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? AnonymizedAt { get; set; }
}
