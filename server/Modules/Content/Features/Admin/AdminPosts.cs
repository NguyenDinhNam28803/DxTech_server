using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Content.Features.Public;
using server.Modules.Content.Jobs;
using server.Modules.Content.Persistence;
using server.Shared.Results;
using server.Shared.Text;

namespace server.Modules.Content.Features.Admin;

/// <summary>Nội dung bài khi tạo/sửa (không gồm loại, slug, trạng thái — có thao tác riêng).</summary>
public sealed record PostInput(
    string? Title, string? Excerpt, string? Label, JsonElement? Blocks, List<string>? KeyPoints,
    Guid? TopicId, Guid? ParentId, Guid? CoverMediaId, string? CoverImageAlt,
    string? SeoTitle, string? SeoDescription, int SortOrder, bool IsFeatured);

public sealed record CreatePostCommand(string? Type, string? Slug, PostInput Content);

public sealed record PublishCommand(DateTimeOffset? PublishedAt);

public sealed record ChangeSlugCommand(string? Slug);

public sealed record AdminPostView(
    Guid Id, string Type, string Slug, string Status, string Title, string? Excerpt, string? Label,
    JsonElement Blocks, IReadOnlyList<string> KeyPoints, Guid? TopicId, Guid? ParentId,
    Guid? CoverMediaId, string? CoverImageUrl, string? CoverImageAlt, string? SeoTitle, string? SeoDescription,
    short? ReadingMinutes, int SortOrder, bool IsFeatured, DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid UpdatedBy, uint Version);

public sealed record AdminPostRow(Guid Id, string Type, string Slug, string Status, string Title, Guid? TopicId, Guid? ParentId,
    DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt, bool IsFeatured);

public sealed record AdminPostPage(IReadOnlyList<AdminPostRow> Items, string? NextCursor);

public sealed record RevisionRow(long Id, string Title, Guid SavedBy, DateTimeOffset SavedAt);

public sealed class AdminListPostsQuery
{
    public string? Type { get; init; }

    public string? Status { get; init; }

    public Guid? TopicId { get; init; }

    public string? Q { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 30;
}

/// <summary>
/// Quản trị bài (ADR-015 §2–3, CT-3). Mỗi lần lưu nội dung = một revision. Sửa đồng thời: client gửi <c>If-Match</c> = <c>version</c>
/// (xmin) đọc được; khác ⇒ 409, người sau tải lại bản mới chứ không ghi đè lặng lẽ. Slug đổi qua thao tác riêng (ghi slug_redirects).
/// Cấu trúc tối đa 2 tầng bài (trang → hạng mục) để khớp URL web 3 cấp.
/// </summary>
public sealed partial class AdminPostsHandler
{
    public const int MaxListLimit = 100;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    internal AdminPostsHandler(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    internal static bool IsSlug(string? s) => s is { Length: > 0 and <= 150 } && SlugPattern().IsMatch(s);

    private static Error Conflict() => Error.Conflict("content.edit_conflict",
        "Bài đã được người khác sửa sau khi bạn mở — tải lại để xem bản mới rồi sửa tiếp.");

    private static Error NotFound() => Error.NotFound("content.post_not_found", "Không tìm thấy bài viết.");

    // ---------- Đọc ----------

    public async Task<Result<AdminPostPage>> ListAsync(AdminListPostsQuery q, CancellationToken ct)
    {
        IQueryable<Post> posts = _db.Set<Post>().AsNoTracking();
        if (q.Type is not null)
        {
            if (!PublicContentHandler.TryParseType(q.Type, out PostType type))
            {
                return Error.Validation("content.invalid_type", "Loại nội dung không hợp lệ.");
            }

            posts = posts.Where(p => p.PostType == type);
        }

        if (q.Status is not null)
        {
            if (!SnakeCaseEnumConverter<PostStatus>.DbValues.Contains(q.Status))
            {
                return Error.Validation("content.invalid_status", "Trạng thái: draft, published hoặc archived.");
            }

            PostStatus status = SnakeCaseEnumConverter<PostStatus>.FromProvider(q.Status);
            posts = posts.Where(p => p.Status == status);
        }

        if (q.TopicId is { } topicId)
        {
            posts = posts.Where(p => p.TopicId == topicId);
        }

        if (SearchText.ToTsQuery(q.Q) is { } ts)
        {
            posts = posts.Where(p => EF.Property<NpgsqlTsVector>(p, PostConfiguration.SearchVector)
                .Matches(EF.Functions.ToTsQuery("simple", ContentDbFunctions.Unaccent(ts))));
        }

        if (!string.IsNullOrEmpty(q.Cursor))
        {
            if (!PublicContentHandler.TryDecodeCursor(q.Cursor, out var c))
            {
                return Error.Validation("content.invalid_query", "Tham số cursor không hợp lệ.");
            }

            posts = posts.Where(p => p.UpdatedAt < c.At || (p.UpdatedAt == c.At && p.Id.CompareTo(c.Id) < 0));
        }

        int limit = Math.Clamp(q.Limit, 1, MaxListLimit);
        List<Post> rows = await posts.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).Take(limit + 1).ToListAsync(ct);
        return new AdminPostPage(
            rows.Take(limit).Select(p => new AdminPostRow(p.Id, TypeName(p.PostType), p.Slug, StatusName(p.Status), p.Title,
                p.TopicId, p.ParentId, p.PublishedAt, p.UpdatedAt, p.IsFeatured)).ToList(),
            rows.Count > limit ? PublicContentHandler.EncodeCursor(rows[limit - 1].UpdatedAt, rows[limit - 1].Id) : null);
    }

    public async Task<Result<AdminPostView>> GetAsync(Guid id, CancellationToken ct)
        => await _db.Set<Post>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } post ? View(post) : NotFound();

    public async Task<Result<IReadOnlyList<RevisionRow>>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        if (!await _db.Set<Post>().AnyAsync(p => p.Id == id, ct))
        {
            return NotFound();
        }

        return await _db.Set<PostRevision>().AsNoTracking().Where(r => r.PostId == id)
            .OrderByDescending(r => r.Id).Take(ContentMaintenance.RevisionsKept)
            .Select(r => new RevisionRow(r.Id, r.Title, r.SavedBy, r.SavedAt)).ToListAsync(ct);
    }

    // ---------- Ghi ----------

    public async Task<Result<AdminPostView>> CreateAsync(CreatePostCommand c, Guid userId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!PublicContentHandler.TryParseType(c.Type, out PostType type))
        {
            errors["type"] = [$"Một trong: {string.Join(", ", SnakeCaseEnumConverter<PostType>.DbValues)}."];
        }

        string slug = (c.Slug ?? "").Trim();
        if (!IsSlug(slug))
        {
            errors["slug"] = ["Chữ thường không dấu, số và dấu gạch ngang (ví dụ: huong-dan-kich-hoat), tối đa 150 ký tự."];
        }
        else if (errors.Count == 0 && await SlugTakenAsync(type, slug, null, ct))
        {
            errors["slug"] = ["Đã có bài khác dùng slug này (hoặc là slug cũ đang chuyển hướng)."];
        }

        var post = new Post(type, slug, "x", userId);
        await ApplyAsync(post, c.Content, errors, ct);
        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_post", "Nội dung bài chưa hợp lệ.", errors);
        }

        _db.Add(post);
        _db.Add(new PostRevision(post, userId));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("content.slug_taken", "Slug vừa được bài khác dùng.");
        }

        return View(post);
    }

    public Task<Result<AdminPostView>> UpdateAsync(Guid id, uint expectedVersion, PostInput input, Guid userId, CancellationToken ct)
        => MutateAsync(id, expectedVersion, userId, ct, async (post, errors) => await ApplyAsync(post, input, errors, ct), revision: true);

    public Task<Result<AdminPostView>> PublishAsync(Guid id, uint expectedVersion, PublishCommand c, Guid userId, CancellationToken ct)
        => MutateAsync(id, expectedVersion, userId, ct, (post, _) =>
        {
            post.Publish((c.PublishedAt ?? _clock.GetUtcNow()).ToUniversalTime());
            return Task.CompletedTask;
        });

    public Task<Result<AdminPostView>> UnpublishAsync(Guid id, uint expectedVersion, bool archive, Guid userId, CancellationToken ct)
        => MutateAsync(id, expectedVersion, userId, ct, (post, _) =>
        {
            post.Status = archive ? PostStatus.Archived : PostStatus.Draft;
            return Task.CompletedTask;
        });

    public Task<Result<AdminPostView>> RestoreAsync(Guid id, long revisionId, uint expectedVersion, Guid userId, CancellationToken ct)
        => MutateAsync(id, expectedVersion, userId, ct, async (post, errors) =>
        {
            PostRevision? rev = await _db.Set<PostRevision>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == revisionId && r.PostId == id, ct);
            if (rev is null)
            {
                errors["revision"] = ["Không có phiên bản này."];
                return;
            }

            // Ảnh trong bản cũ có thể đã bị xoá ⇒ kiểm lại như lần lưu bình thường.
            await CheckBlocksAsync(rev.Blocks, errors, ct);
            post.Title = rev.Title;
            post.Blocks = rev.Blocks;
            post.KeyPoints = [.. rev.KeyPoints];
            post.ReadingMinutes = ContentBlocks.ReadingMinutes(ContentBlocks.Validate(rev.Blocks).PlainText);
        }, revision: true);

    public Task<Result<AdminPostView>> ChangeSlugAsync(Guid id, uint expectedVersion, ChangeSlugCommand c, Guid userId, CancellationToken ct)
        => MutateAsync(id, expectedVersion, userId, ct, async (post, errors) =>
        {
            string slug = (c.Slug ?? "").Trim();
            if (!IsSlug(slug))
            {
                errors["slug"] = ["Slug không hợp lệ."];
                return;
            }

            if (slug == post.Slug)
            {
                return;
            }

            if (await _db.Set<Post>().IgnoreQueryFilters().AnyAsync(p => p.PostType == post.PostType && p.Slug == slug && p.Id != post.Id && p.DeletedAt == null, ct))
            {
                errors["slug"] = ["Đã có bài khác dùng slug này."];
                return;
            }

            // Slug mới từng là slug cũ (của bài này hoặc bài khác) ⇒ bỏ chuyển hướng đó, URL thuộc về bài này.
            await _db.Set<SlugRedirect>().Where(r => r.PostType == post.PostType && r.OldSlug == slug).ExecuteDeleteAsync(ct);
            _db.Add(new SlugRedirect(post.PostType, post.Slug, post.Id));
            post.Slug = slug;
        });

    public async Task<Result> DeleteAsync(Guid id, uint expectedVersion, Guid userId, CancellationToken ct)
    {
        if (await _db.Set<Post>().AnyAsync(p => p.ParentId == id, ct))
        {
            return Error.Conflict("content.post_has_children", "Bài còn hạng mục con — xoá hoặc chuyển các hạng mục trước.");
        }

        Result<AdminPostView> r = await MutateAsync(id, expectedVersion, userId, ct, (post, _) =>
        {
            post.DeletedAt = _clock.GetUtcNow();
            return Task.CompletedTask;
        });
        return r.IsSuccess ? Result.Success() : r.Error;
    }

    // ---------- Hỗ trợ ----------

    private async Task<Result<AdminPostView>> MutateAsync(Guid id, uint expectedVersion, Guid userId, CancellationToken ct,
        Func<Post, Dictionary<string, string[]>, Task> change, bool revision = false)
    {
        // Một transaction: thao tác phụ (xoá chuyển hướng khi đổi slug) không được ghi nếu lưu bài thất bại.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        Post? post = await _db.Set<Post>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return NotFound();
        }

        if (post.Version != expectedVersion)
        {
            return Conflict();
        }

        // Hai lớp: so version client gửi với bản vừa đọc (người khác đã lưu trước đó), rồi EF kèm "WHERE xmin = <đã đọc>"
        // khi UPDATE (người khác lưu xen giữa lúc đọc và lúc ghi) ⇒ DbUpdateConcurrencyException ⇒ 409.
        var errors = new Dictionary<string, string[]>();
        await change(post, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_post", "Nội dung bài chưa hợp lệ.", errors);
        }

        post.UpdatedBy = userId;
        if (revision)
        {
            _db.Add(new PostRevision(post, userId));
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("content.slug_taken", "Slug vừa được bài khác dùng.");
        }

        return View(post);
    }

    private async Task ApplyAsync(Post post, PostInput input, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        string title = (input.Title ?? "").Trim();
        if (title.Length is < 1 or > Post.TitleMaxLength)
        {
            errors["title"] = [$"Tiêu đề 1–{Post.TitleMaxLength} ký tự."];
        }

        CheckLength(errors, "excerpt", input.Excerpt, Post.ExcerptMaxLength);
        CheckLength(errors, "label", input.Label, Post.LabelMaxLength);
        CheckLength(errors, "seoTitle", input.SeoTitle, Post.SeoTitleMaxLength);
        CheckLength(errors, "seoDescription", input.SeoDescription, Post.SeoDescriptionMaxLength);
        CheckLength(errors, "coverImageAlt", input.CoverImageAlt, 200);

        List<string> keyPoints = (input.KeyPoints ?? []).Select(k => k?.Trim() ?? "").Where(k => k.Length > 0).ToList();
        if (keyPoints.Count > Post.MaxKeyPoints || keyPoints.Any(k => k.Length > Post.KeyPointMaxLength))
        {
            errors["keyPoints"] = [$"Tối đa {Post.MaxKeyPoints} ý, mỗi ý ≤ {Post.KeyPointMaxLength} ký tự."];
        }

        string blocks = input.Blocks is { ValueKind: not JsonValueKind.Undefined } b ? b.GetRawText() : "[]";
        BlocksCheck check = await CheckBlocksAsync(blocks, errors, ct);

        if (input.TopicId is { } topicId
            && !await _db.Set<Topic>().AnyAsync(t => t.Id == topicId && t.PostType == post.PostType, ct))
        {
            errors["topicId"] = ["Chủ đề không tồn tại hoặc khác loại nội dung."];
        }

        if (input.ParentId is { } parentId)
        {
            var parent = await _db.Set<Post>().Where(p => p.Id == parentId)
                .Select(p => new { p.PostType, p.ParentId }).SingleOrDefaultAsync(ct);
            bool hasChildren = await _db.Set<Post>().AnyAsync(p => p.ParentId == post.Id, ct);
            if (parent is null || parent.PostType != post.PostType || parentId == post.Id)
            {
                errors["parentId"] = ["Bài cha không tồn tại hoặc khác loại nội dung."];
            }
            else if (parent.ParentId is not null || hasChildren)
            {
                errors["parentId"] = ["Chỉ 2 tầng: trang → hạng mục (bài cha không được là hạng mục, bài có hạng mục không làm con được)."];
            }
        }

        if (input.CoverMediaId is { } coverId && !await _db.Set<MediaFile>().AnyAsync(m => m.Id == coverId, ct))
        {
            errors["coverMediaId"] = ["Ảnh không tồn tại hoặc đã xoá."];
        }

        if (errors.Count > 0)
        {
            return;
        }

        post.Title = title;
        post.Excerpt = Clean(input.Excerpt);
        post.Label = Clean(input.Label);
        post.Blocks = blocks;
        post.KeyPoints = keyPoints;
        post.TopicId = input.TopicId;
        post.ParentId = input.ParentId;
        if (input.CoverMediaId is not null)
        {
            post.CoverImageUrl = null; // đã có ảnh trong thư viện ⇒ bỏ URL ngoài của dữ liệu cũ
        }

        post.CoverMediaId = input.CoverMediaId;
        post.CoverImageAlt = Clean(input.CoverImageAlt);
        post.SeoTitle = Clean(input.SeoTitle);
        post.SeoDescription = Clean(input.SeoDescription);
        post.SortOrder = input.SortOrder;
        post.IsFeatured = input.IsFeatured;
        post.ReadingMinutes = check.PlainText.Length > 0 ? ContentBlocks.ReadingMinutes(check.PlainText) : null;
    }

    private async Task<BlocksCheck> CheckBlocksAsync(string blocks, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        BlocksCheck check = ContentBlocks.Validate(blocks);
        if (!check.IsValid)
        {
            errors["blocks"] = [.. check.Errors.Take(20)];
            return check;
        }

        if (check.MediaIds.Count > 0)
        {
            List<Guid> ids = [.. check.MediaIds];
            int found = await _db.Set<MediaFile>().CountAsync(m => ids.Contains(m.Id), ct);
            if (found != ids.Count)
            {
                errors["blocks"] = ["Có khối ảnh tham chiếu ảnh không tồn tại hoặc đã xoá."];
            }
        }

        return check;
    }

    private async Task<bool> SlugTakenAsync(PostType type, string slug, Guid? exceptId, CancellationToken ct)
        => await _db.Set<Post>().AnyAsync(p => p.PostType == type && p.Slug == slug && p.Id != exceptId, ct)
           || await _db.Set<SlugRedirect>().AnyAsync(r => r.PostType == type && r.OldSlug == slug, ct);

    private static void CheckLength(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (value is not null && value.Trim().Length > max)
        {
            errors[field] = [$"Tối đa {max} ký tự."];
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string TypeName(PostType t) => SnakeCaseEnumConverter<PostType>.ToProvider(t);

    internal static string StatusName(PostStatus s) => SnakeCaseEnumConverter<PostStatus>.ToProvider(s);

    private static AdminPostView View(Post p)
    {
        using JsonDocument blocks = JsonDocument.Parse(p.Blocks);
        return new AdminPostView(p.Id, TypeName(p.PostType), p.Slug, StatusName(p.Status), p.Title, p.Excerpt, p.Label,
            blocks.RootElement.Clone(), p.KeyPoints, p.TopicId, p.ParentId, p.CoverMediaId, p.CoverImageUrl, p.CoverImageAlt,
            p.SeoTitle, p.SeoDescription, p.ReadingMinutes, p.SortOrder, p.IsFeatured, p.PublishedAt, p.CreatedAt, p.UpdatedAt,
            p.UpdatedBy, p.Version);
    }

    internal static string ETag(uint version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    internal static bool TryParseETag(string? header, out uint version)
    {
        version = 0;
        string? v = header?.Trim();
        if (v is null || v.Length < 3 || v[0] != '"' || v[^1] != '"')
        {
            return false;
        }

        return uint.TryParse(v[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
