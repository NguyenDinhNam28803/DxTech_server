using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Identity.Contracts;

namespace server.Modules.Content.Seeding;

internal sealed record ContentSeedReport(
    int TopicsCreated, int TopicsSkipped, int PostsCreated, int PostsSkipped, string Source, string AuthorEmail, bool DryRun);

/// <summary>
/// Nạp nội dung web hiện có (ADR-015 §8) từ <c>content-seed.json</c> — bản xuất của <c>articles.ts</c>, <c>solutions.ts</c>,
/// <c>resources.ts</c>, <c>ResourceView.vue</c> bằng <c>tools/content-export/export-content.mjs</c>.
/// <list type="bullet">
/// <item>Giữ ĐÚNG slug đang chạy ⇒ không đổi URL. Bài được đăng ngay (đang hiển thị trên web).</item>
/// <item>"Chỉ thêm cái còn thiếu" như <c>--seed-demo</c>: chủ đề/bài đã có (kể cả đã xoá mềm) thì bỏ qua — chạy lại không
///       nhân đôi, không ghi đè sửa đổi của biên tập viên, không hồi sinh bài đã xoá.</item>
/// <item>Mỗi bài tạo kèm một revision (điểm khôi phục ban đầu). Khối được kiểm bằng <see cref="ContentBlocks"/> như khi lưu từ admin.</item>
/// <item>Tác giả = admin đang hoạt động tạo sớm nhất. Một transaction: lỗi một dòng ⇒ không ghi gì; <c>dryRun</c> chạy đủ rồi rollback.</item>
/// </list>
/// </summary>
internal sealed class ContentSeeder(AppDbContext db, IUserProfiles users, TimeProvider clock)
{
    public const string ResourceName = "server.Modules.Content.Seeding.content-seed.json";

    public async Task<ContentSeedReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        SeedFile data = Load();
        UserProfileRef author = await users.GetOldestActiveAdminAsync(ct)
            ?? throw new InvalidOperationException("Chưa có admin đang hoạt động — chạy --create-admin trước (bài cần tác giả).");
        DateTimeOffset now = clock.GetUtcNow();

        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

        List<Topic> existingTopics = await db.Set<Topic>().IgnoreQueryFilters().ToListAsync(ct);
        var topicByKey = new Dictionary<string, Topic>(StringComparer.Ordinal);
        int topicsCreated = 0, topicsSkipped = 0;
        foreach (SeedTopic seed in data.Topics)
        {
            PostType type = SnakeCaseEnumConverter<PostType>.FromProvider(seed.PostType);
            Guid? parentId = null;
            if (seed.ParentKey is not null)
            {
                parentId = (topicByKey.GetValueOrDefault(seed.ParentKey)
                    ?? throw new InvalidOperationException($"Chủ đề '{seed.Key}': chủ đề cha '{seed.ParentKey}' phải đứng trước.")).Id;
            }

            Topic? existing = existingTopics.FirstOrDefault(t => t.PostType == type && t.ParentId == parentId && t.Slug == seed.Slug);
            if (existing is not null)
            {
                topicByKey[seed.Key] = existing;
                topicsSkipped++;
                continue;
            }

            var topic = new Topic(type, seed.Slug, seed.Name, parentId)
            {
                Description = seed.Description,
                ImageUrl = seed.ImageUrl,
                SortOrder = seed.SortOrder,
            };
            db.Add(topic);
            existingTopics.Add(topic);
            topicByKey[seed.Key] = topic;
            topicsCreated++;
        }

        Dictionary<(PostType, string), Guid> postIds = (await db.Set<Post>().IgnoreQueryFilters()
                .Select(p => new { p.PostType, p.Slug, p.Id }).ToListAsync(ct))
            .ToDictionary(p => (p.PostType, p.Slug), p => p.Id);

        int postsCreated = 0, postsSkipped = 0;
        foreach (SeedPost seed in data.Posts)
        {
            PostType type = SnakeCaseEnumConverter<PostType>.FromProvider(seed.PostType);
            if (postIds.ContainsKey((type, seed.Slug)))
            {
                postsSkipped++;
                continue;
            }

            string blocks = seed.Blocks.GetRawText();
            BlocksCheck check = ContentBlocks.Validate(blocks);
            if (!check.IsValid)
            {
                throw new InvalidOperationException($"Bài {seed.PostType}/{seed.Slug}: khối không hợp lệ — {string.Join("; ", check.Errors.Take(3))}");
            }

            if (check.MediaIds.Count > 0)
            {
                throw new InvalidOperationException($"Bài {seed.PostType}/{seed.Slug}: dữ liệu seed không được tham chiếu ảnh trong media.");
            }

            Guid? parentId = null;
            if (seed.ParentSlug is not null)
            {
                parentId = postIds.TryGetValue((type, seed.ParentSlug), out Guid pid)
                    ? pid
                    : throw new InvalidOperationException($"Bài {seed.PostType}/{seed.Slug}: bài cha '{seed.ParentSlug}' phải đứng trước.");
            }

            var post = new Post(type, seed.Slug, seed.Title, author.Id)
            {
                TopicId = seed.TopicKey is null ? null : (topicByKey.GetValueOrDefault(seed.TopicKey)
                    ?? throw new InvalidOperationException($"Bài {seed.PostType}/{seed.Slug}: không có chủ đề '{seed.TopicKey}'.")).Id,
                ParentId = parentId,
                Excerpt = seed.Excerpt,
                Label = seed.Label,
                Blocks = blocks,
                KeyPoints = [.. seed.KeyPoints],
                CoverImageUrl = seed.CoverImageUrl,
                CoverImageAlt = seed.CoverImageAlt,
                ReadingMinutes = seed.ReadingMinutes ?? (check.PlainText.Length > 0 ? ContentBlocks.ReadingMinutes(check.PlainText) : null),
                SortOrder = seed.SortOrder,
                IsFeatured = seed.IsFeatured,
            };
            post.Publish((seed.PublishedAt ?? now).ToUniversalTime()); // Npgsql chỉ nhận timestamptz offset 0
            db.Add(post);
            db.Add(new PostRevision(post, author.Id));
            postIds[(type, seed.Slug)] = post.Id;
            postsCreated++;
        }

        await db.SaveChangesAsync(ct);
        if (dryRun)
        {
            await tx.RollbackAsync(ct);
        }
        else
        {
            await tx.CommitAsync(ct);
        }

        return new ContentSeedReport(topicsCreated, topicsSkipped, postsCreated, postsSkipped, data.Source, author.Email, dryRun);
    }

    internal static SeedFile Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Không thấy resource {ResourceName}.");
        SeedFile data = JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("content-seed.json rỗng.");
        if (data.Version != 1)
        {
            throw new InvalidOperationException($"content-seed.json phiên bản {data.Version} không được hỗ trợ.");
        }

        return data;
    }

    internal sealed record SeedFile(int Version, string Source, List<SeedTopic> Topics, List<SeedPost> Posts);

    internal sealed record SeedTopic(string Key, string PostType, string? ParentKey, string Slug, string Name,
        string? Description, string? ImageUrl, int SortOrder);

    internal sealed record SeedPost(string PostType, string? TopicKey, string? ParentSlug, string Slug, string Title,
        string? Excerpt, string? Label, DateTimeOffset? PublishedAt, short? ReadingMinutes, string? CoverImageUrl,
        string? CoverImageAlt, List<string> KeyPoints, int SortOrder, bool IsFeatured, JsonElement Blocks);
}
