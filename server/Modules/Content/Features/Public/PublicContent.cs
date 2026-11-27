using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Content.Persistence;
using server.Shared.Results;
using server.Shared.Text;

namespace server.Modules.Content.Features.Public;

// ---------- Kiểu trả về (JSON camelCase) ----------

public sealed record PostLink(string Type, string Slug, string Title, string? Excerpt, string? Path);

/// <summary>
/// Trang cấp 1 của một chủ đề, đủ để web dựng menu + thẻ mà không phải gọi chi tiết từng trang (CT-C1):
/// ý chính, ghi chú nổi bật (khối callout đầu tiên — ví dụ "Đối tượng phù hợp: …") và các hạng mục con đã đăng.
/// </summary>
public sealed record TopicPage(string Type, string Slug, string Title, string? Excerpt, string? Path,
    IReadOnlyList<string> KeyPoints, string? Callout, IReadOnlyList<PostLink> Children);

public sealed record TopicNode(string Slug, string Name, string? Description, string? ImageUrl, string? Path,
    IReadOnlyList<TopicNode> Children, IReadOnlyList<TopicPage> Pages);

public sealed record CoverImage(string Url, string? Alt);

public sealed record TopicRef(string Slug, string Name, string? Path);

/// <summary>Thẻ bài. <see cref="Blocks"/>/<see cref="KeyPoints"/> chỉ có khi gọi danh sách với <c>include=blocks</c> (thẻ thuật ngữ, sách, tài liệu).</summary>
public sealed record PostSummary(string Type, string Slug, string Title, string? Excerpt, string? Label,
    DateTimeOffset PublishedAt, short? ReadingMinutes, CoverImage? Cover, TopicRef? Topic, string? Path,
    JsonElement? Blocks = null, IReadOnlyList<string>? KeyPoints = null);

public sealed record PostPage(IReadOnlyList<PostSummary> Items, string? NextCursor);

public sealed record MediaRef(string Url, int Width, int Height);

public sealed record PostDetail(
    string Type, string Slug, string Title, string? Excerpt, string? Label,
    JsonElement Blocks, IReadOnlyList<string> KeyPoints,
    CoverImage? Cover, IReadOnlyDictionary<Guid, MediaRef> Media,
    DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt, short? ReadingMinutes,
    string? SeoTitle, string? SeoDescription,
    IReadOnlyList<TopicRef> Breadcrumb, PostLink? Parent, IReadOnlyList<PostLink> Children, string? Path);

public sealed record CourseSessionView(Guid Id, DateOnly StartsOn, DateOnly? EndsOn, string Format, string? Location,
    int? Seats, long? PriceVnd, string Status);

/// <summary>Kết quả tra chi tiết: bài, hoặc slug cũ ⇒ slug mới (API trả 301).</summary>
public sealed record PostLookup(PostDetail? Post, string? MovedToSlug);

public sealed class ListPostsQuery
{
    public string? Type { get; init; }

    /// <summary>Slug chủ đề (gồm cả chủ đề con).</summary>
    public string? Topic { get; init; }

    public string? Q { get; init; }

    /// <summary><c>newest</c> (mặc định, phân trang bằng cursor) · <c>order</c> (thứ tự biên tập, không cursor).</summary>
    public string? Sort { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 20;

    /// <summary><c>blocks</c> ⇒ mỗi thẻ kèm khối nội dung + ý chính (cho danh sách ngắn hiển thị đầy đủ, ví dụ thuật ngữ). Tối đa 100 thẻ như thường.</summary>
    public string? Include { get; init; }
}

/// <summary>
/// Đọc nội dung công khai (ADR-015, CT-2). Luật hiển thị duy nhất: <c>status = published</c>, <c>published_at &lt;= now</c>,
/// chưa xoá — bản nháp, bài lưu trữ, bài hẹn giờ chưa tới giờ đều như không tồn tại (404, không lộ qua danh sách/sitemap).
/// </summary>
public sealed class PublicContentHandler
{
    public const int MaxLimit = 100;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ContentOptions _options;

    internal PublicContentHandler(AppDbContext db, TimeProvider clock, IOptions<ContentOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    internal IQueryable<Post> Visible()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        return _db.Set<Post>().AsNoTracking().Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now);
    }

    internal static bool TryParseType(string? value, out PostType type)
    {
        if (value is null || !SnakeCaseEnumConverter<PostType>.DbValues.Contains(value))
        {
            type = default;
            return false;
        }

        type = SnakeCaseEnumConverter<PostType>.FromProvider(value);
        return true;
    }

    private static Error BadType() => Error.Validation("content.invalid_type", "Loại nội dung không hợp lệ.",
        new Dictionary<string, string[]> { ["type"] = [$"Một trong: {string.Join(", ", SnakeCaseEnumConverter<PostType>.DbValues)}."] });

    /// <summary>Chủ đề đang bật của một loại, kèm chuỗi slug từ gốc (dùng dựng path/breadcrumb).</summary>
    internal async Task<TopicIndex> TopicsAsync(PostType type, CancellationToken ct)
        => new(await _db.Set<Topic>().AsNoTracking().Where(t => t.PostType == type && t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct));

    // ---------- GET /api/content/topics?type= ----------

    public async Task<Result<IReadOnlyList<TopicNode>>> TopicTreeAsync(string? typeValue, CancellationToken ct)
    {
        if (!TryParseType(typeValue, out PostType type))
        {
            return BadType();
        }

        TopicIndex topics = await TopicsAsync(type, ct);
        var pages = (await Visible()
                .Where(p => p.PostType == type && p.ParentId == null && p.TopicId != null)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
                .Select(p => new { p.Id, p.TopicId, p.Slug, p.Title, p.Excerpt, p.KeyPoints, p.Blocks })
                .ToListAsync(ct))
            .ToLookup(p => p.TopicId!.Value);

        // Hạng mục con đã đăng của mọi trang — một truy vấn cho cả cây (không N+1).
        var children = (await Visible()
                .Where(p => p.PostType == type && p.ParentId != null)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
                .Select(p => new { ParentId = p.ParentId!.Value, p.Slug, p.Title, p.Excerpt })
                .ToListAsync(ct))
            .ToLookup(c => c.ParentId);

        TopicNode Node(Topic t)
        {
            IReadOnlyList<string> chain = topics.Chain(t.Id);
            return new TopicNode(t.Slug, t.Name, t.Description, t.ImageUrl, WebPaths.Topic(type, chain),
                topics.ChildrenOf(t.Id).Select(Node).ToList(),
                pages[t.Id].Select(p => new TopicPage(typeValue!, p.Slug, p.Title, p.Excerpt, WebPaths.Post(type, p.Slug, chain, null),
                    p.KeyPoints, FirstCallout(p.Blocks),
                    children[p.Id].Select(c => new PostLink(typeValue!, c.Slug, c.Title, c.Excerpt, WebPaths.Post(type, c.Slug, chain, p.Slug))).ToList()))
                .ToList());
        }

        return topics.ChildrenOf(null).Select(Node).ToList();
    }

    // ---------- GET /api/content/posts ----------

    public async Task<Result<PostPage>> ListAsync(ListPostsQuery query, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseType(query.Type, out PostType type))
        {
            return BadType();
        }

        string sort = query.Sort ?? "newest";
        if (sort is not ("newest" or "order"))
        {
            errors["sort"] = ["newest hoặc order."];
        }

        (DateTimeOffset At, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            if (sort != "newest" || !TryDecodeCursor(query.Cursor, out var c))
            {
                errors["cursor"] = ["Không hợp lệ."];
            }
            else
            {
                after = c;
            }
        }

        bool withBlocks = query.Include == "blocks";
        if (query.Include is not null && !withBlocks)
        {
            errors["include"] = ["Chỉ hỗ trợ include=blocks."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_query", "Tham số không hợp lệ.", errors);
        }

        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        TopicIndex topics = await TopicsAsync(type, ct);
        IQueryable<Post> posts = Visible().Where(p => p.PostType == type);

        if (!string.IsNullOrWhiteSpace(query.Topic))
        {
            List<Guid> ids = topics.WithDescendants(query.Topic.Trim());
            if (ids.Count == 0)
            {
                return Error.NotFound("content.topic_not_found", "Không có chủ đề này.");
            }

            posts = posts.Where(p => p.TopicId != null && ids.Contains(p.TopicId.Value));
        }

        string? tsQuery = SearchText.ToTsQuery(query.Q);
        if (tsQuery is not null)
        {
            posts = posts.Where(p => EF.Property<NpgsqlTsVector>(p, PostConfiguration.SearchVector)
                .Matches(EF.Functions.ToTsQuery("simple", ContentDbFunctions.Unaccent(tsQuery))));
        }

        if (after is { } a)
        {
            posts = posts.Where(p => p.PublishedAt < a.At || (p.PublishedAt == a.At && p.Id.CompareTo(a.Id) < 0));
        }

        IQueryable<Post> ordered = sort == "order"
            ? posts.OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            : posts.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id);

        var rows = await ordered.Take(limit + 1).Select(p => new
        {
            p.Id, p.Slug, p.Title, p.Excerpt, p.Label, p.PublishedAt, p.ReadingMinutes, p.CoverImageUrl, p.CoverImageAlt,
            p.CoverMediaId, p.TopicId, ParentSlug = _db.Set<Post>().Where(x => x.Id == p.ParentId).Select(x => x.Slug).FirstOrDefault(),
            Blocks = withBlocks ? p.Blocks : null, KeyPoints = withBlocks ? p.KeyPoints : null,
        }).ToListAsync(ct);

        Dictionary<Guid, MediaRef> media = await MediaAsync(rows.Where(r => r.CoverMediaId != null).Select(r => r.CoverMediaId!.Value), ct);
        List<PostSummary> items = rows.Take(limit).Select(r =>
        {
            IReadOnlyList<string> chain = r.TopicId is { } tid ? topics.Chain(tid) : [];
            return new PostSummary(query.Type!, r.Slug, r.Title, r.Excerpt, r.Label, r.PublishedAt!.Value, r.ReadingMinutes,
                Cover(r.CoverMediaId, r.CoverImageUrl, r.CoverImageAlt, media),
                r.TopicId is { } t2 ? topics.Ref(t2, type) : null,
                WebPaths.Post(type, r.Slug, chain, r.ParentSlug),
                r.Blocks is null ? null : ParseBlocks(r.Blocks), r.KeyPoints);
        }).ToList();

        string? next = sort == "newest" && rows.Count > limit
            ? EncodeCursor(rows[limit - 1].PublishedAt!.Value, rows[limit - 1].Id)
            : null;
        return new PostPage(items, next);
    }

    // ---------- GET /api/content/posts/{type}/{slug} ----------

    public async Task<Result<PostLookup>> GetAsync(string typeValue, string slug, CancellationToken ct)
    {
        if (!TryParseType(typeValue, out PostType type))
        {
            return BadType();
        }

        Post? post = await Visible().SingleOrDefaultAsync(p => p.PostType == type && p.Slug == slug, ct);
        if (post is null)
        {
            // Slug cũ ⇒ 301 sang slug hiện tại (chỉ khi bài đích đang hiển thị — không lộ slug của bản nháp).
            string? moved = await _db.Set<SlugRedirect>().AsNoTracking()
                .Where(r => r.PostType == type && r.OldSlug == slug)
                .Join(Visible(), r => r.PostId, p => p.Id, (r, p) => p.Slug)
                .FirstOrDefaultAsync(ct);
            return moved is null
                ? Error.NotFound("content.post_not_found", "Không tìm thấy bài viết.")
                : new PostLookup(null, moved);
        }

        TopicIndex topics = await TopicsAsync(type, ct);
        IReadOnlyList<string> chain = post.TopicId is { } tid ? topics.Chain(tid) : [];

        PostLink? parent = null;
        if (post.ParentId is { } pid)
        {
            parent = await Visible().Where(p => p.Id == pid)
                .Select(p => new PostLink(typeValue, p.Slug, p.Title, p.Excerpt, WebPaths.Post(type, p.Slug, chain, null)))
                .FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return Error.NotFound("content.post_not_found", "Không tìm thấy bài viết."); // trang cha bị ẩn ⇒ ẩn cả hạng mục
            }
        }

        List<PostLink> children = (await Visible().Where(p => p.ParentId == post.Id)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
                .Select(p => new { p.Slug, p.Title, p.Excerpt }).ToListAsync(ct))
            .Select(c => new PostLink(typeValue, c.Slug, c.Title, c.Excerpt, WebPaths.Post(type, c.Slug, chain, post.Slug)))
            .ToList();

        using JsonDocument blocks = JsonDocument.Parse(post.Blocks);
        BlocksCheck check = ContentBlocks.Validate(post.Blocks);
        Dictionary<Guid, MediaRef> media = await MediaAsync(check.MediaIds.Concat(post.CoverMediaId is { } m ? [m] : []), ct);

        var breadcrumb = new List<TopicRef>();
        if (post.TopicId is { } t)
        {
            breadcrumb.AddRange(topics.Ancestry(t).Select(x => topics.Ref(x.Id, type)));
        }

        return new PostLookup(new PostDetail(
            typeValue, post.Slug, post.Title, post.Excerpt, post.Label,
            blocks.RootElement.Clone(), post.KeyPoints,
            Cover(post.CoverMediaId, post.CoverImageUrl, post.CoverImageAlt, media), media,
            post.PublishedAt!.Value, post.UpdatedAt, post.ReadingMinutes, post.SeoTitle, post.SeoDescription,
            breadcrumb, parent, children, WebPaths.Post(type, post.Slug, chain, parent?.Slug)), null);
    }

    // ---------- GET /api/content/featured?type= ----------

    public async Task<Result<IReadOnlyList<PostSummary>>> FeaturedAsync(string? typeValue, int limit, CancellationToken ct)
    {
        if (!TryParseType(typeValue, out PostType type))
        {
            return BadType();
        }

        TopicIndex topics = await TopicsAsync(type, ct);
        var rows = await Visible().Where(p => p.PostType == type && p.IsFeatured)
            .OrderBy(p => p.SortOrder).ThenByDescending(p => p.PublishedAt)
            .Take(Math.Clamp(limit, 1, 24))
            .Select(p => new
            {
                p.Slug, p.Title, p.Excerpt, p.Label, p.PublishedAt, p.ReadingMinutes, p.CoverImageUrl, p.CoverImageAlt, p.CoverMediaId, p.TopicId,
                ParentSlug = _db.Set<Post>().Where(x => x.Id == p.ParentId).Select(x => x.Slug).FirstOrDefault(),
            })
            .ToListAsync(ct);
        Dictionary<Guid, MediaRef> media = await MediaAsync(rows.Where(r => r.CoverMediaId != null).Select(r => r.CoverMediaId!.Value), ct);
        return rows.Select(r => new PostSummary(typeValue!, r.Slug, r.Title, r.Excerpt, r.Label, r.PublishedAt!.Value, r.ReadingMinutes,
                Cover(r.CoverMediaId, r.CoverImageUrl, r.CoverImageAlt, media),
                r.TopicId is { } t ? topics.Ref(t, type) : null,
                WebPaths.Post(type, r.Slug, r.TopicId is { } t2 ? topics.Chain(t2) : [], r.ParentSlug)))
            .ToList();
    }

    // ---------- GET /api/content/courses/{slug}/sessions ----------

    public async Task<Result<IReadOnlyList<CourseSessionView>>> SessionsAsync(string slug, CancellationToken ct)
    {
        Guid? postId = await Visible().Where(p => p.PostType == PostType.Training && p.Slug == slug).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (postId is null)
        {
            return Error.NotFound("content.course_not_found", "Không tìm thấy khoá học.");
        }

        // Lịch theo ngày Việt Nam: khoá khai giảng hôm nay vẫn hiển thị cả ngày.
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
        return (await _db.Set<CourseSession>().AsNoTracking()
                .Where(s => s.PostId == postId && s.Status != CourseSessionStatus.Closed && s.StartsOn >= today)
                .OrderBy(s => s.StartsOn).ThenBy(s => s.Id)
                .ToListAsync(ct))
            .Select(s => new CourseSessionView(s.Id, s.StartsOn, s.EndsOn, SnakeCaseEnumConverter<CourseFormat>.ToProvider(s.Format),
                s.Location, s.Seats, s.PriceVnd, SnakeCaseEnumConverter<CourseSessionStatus>.ToProvider(s.Status)))
            .ToList();
    }

    // ---------- Hỗ trợ ----------

    private static JsonElement ParseBlocks(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Chữ của khối callout đầu tiên (nối các đoạn chữ), null nếu không có.</summary>
    internal static string? FirstCallout(string blocks)
    {
        using JsonDocument doc = JsonDocument.Parse(blocks);
        foreach (JsonElement b in doc.RootElement.EnumerateArray())
        {
            if (b.TryGetProperty("type", out JsonElement type) && type.GetString() == "callout" && b.TryGetProperty("spans", out JsonElement spans))
            {
                return string.Concat(spans.EnumerateArray().Select(s => s.TryGetProperty("text", out JsonElement t) ? t.GetString() : ""));
            }
        }

        return null;
    }

    private async Task<Dictionary<Guid, MediaRef>> MediaAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        List<Guid> list = ids.Distinct().ToList();
        if (list.Count == 0)
        {
            return [];
        }

        string baseUrl = _options.MediaBaseUrl.EndsWith('/') ? _options.MediaBaseUrl : _options.MediaBaseUrl + "/";
        return (await _db.Set<MediaFile>().AsNoTracking().Where(m => list.Contains(m.Id)).ToListAsync(ct))
            .ToDictionary(m => m.Id, m => new MediaRef(baseUrl + m.RelativePath, m.Width, m.Height));
    }

    private static CoverImage? Cover(Guid? mediaId, string? url, string? alt, IReadOnlyDictionary<Guid, MediaRef> media)
        => mediaId is { } id && media.TryGetValue(id, out MediaRef? m) ? new CoverImage(m.Url, alt)
            : url is not null ? new CoverImage(url, alt) : null;

    internal static string EncodeCursor(DateTimeOffset at, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}"));

    internal static bool TryDecodeCursor(string cursor, out (DateTimeOffset At, Guid Id) value)
    {
        value = default;
        try
        {
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                && ticks is > 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks && Guid.TryParseExact(parts[1], "N", out Guid id))
            {
                value = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}

/// <summary>Cây chủ đề của một loại trong bộ nhớ (vài chục dòng) — dựng path, breadcrumb, lọc theo chủ đề con.</summary>
internal sealed class TopicIndex(IReadOnlyList<Topic> topics)
{
    private readonly Dictionary<Guid, Topic> _byId = topics.ToDictionary(t => t.Id);

    public IEnumerable<Topic> ChildrenOf(Guid? parentId) => topics.Where(t => t.ParentId == parentId);

    /// <summary>Từ gốc tới chủ đề. Chủ đề cha bị tắt ⇒ chuỗi rỗng (coi như cả nhánh bị ẩn).</summary>
    public IReadOnlyList<Topic> Ancestry(Guid id)
    {
        var chain = new List<Topic>();
        Guid? current = id;
        while (current is { } c)
        {
            if (!_byId.TryGetValue(c, out Topic? t) || chain.Count > 10)
            {
                return [];
            }

            chain.Insert(0, t);
            current = t.ParentId;
        }

        return chain;
    }

    public IReadOnlyList<string> Chain(Guid id) => Ancestry(id).Select(t => t.Slug).ToList();

    public TopicRef Ref(Guid id, PostType type)
        => _byId.TryGetValue(id, out Topic? t) ? new TopicRef(t.Slug, t.Name, WebPaths.Topic(type, Chain(id))) : new TopicRef("", "", null);

    /// <summary>Mọi chủ đề có slug này + toàn bộ con cháu.</summary>
    public List<Guid> WithDescendants(string slug)
    {
        var result = new List<Guid>();
        var queue = new Queue<Guid>(topics.Where(t => t.Slug == slug).Select(t => t.Id));
        while (queue.TryDequeue(out Guid id))
        {
            result.Add(id);
            foreach (Topic child in topics.Where(t => t.ParentId == id))
            {
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }
}
