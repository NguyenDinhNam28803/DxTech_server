using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Content.Domain;

namespace server.IntegrationTests.Content;

/// <summary>
/// CT-2: API nội dung công khai (ADR-015). Tiêu chí thiết kế CT mục 5: bản nháp/hẹn giờ không lộ; slug cũ ⇒ 301;
/// lead spam bị chặn; tìm không dấu. Mỗi test tạo chủ đề + bài slug riêng ⇒ không phụ thuộc dữ liệu test khác.
/// </summary>
public class ContentApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private Guid _author;

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        _author = server.Shared.Ids.UuidV7.NewGuid();
        await Sql("INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp) " +
                  $"VALUES ('{_author}', 'author-{_author:N}@dxtechs.test', repeat('h', 40), 'Tác giả', 'editor', 'active', 0, gen_random_uuid())");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Drafts_archived_scheduled_and_deleted_posts_do_not_exist_publicly()
    {
        Topic topic = await TopicAsync(PostType.News);
        Post live = await PostAsync(PostType.News, topic, "Đang hiển thị");
        Post draft = await PostAsync(PostType.News, topic, "Nháp", status: PostStatus.Draft);
        Post archived = await PostAsync(PostType.News, topic, "Lưu trữ", status: PostStatus.Archived, publishedAt: DateTimeOffset.UtcNow.AddDays(-1));
        Post scheduled = await PostAsync(PostType.News, topic, "Hẹn giờ", publishedAt: DateTimeOffset.UtcNow.AddHours(1));
        Post deleted = await PostAsync(PostType.News, topic, "Đã xoá", deleted: true);

        HttpClient c = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/content/posts/news/{live.Slug}")).StatusCode);
        foreach (Post hidden in new[] { draft, archived, scheduled, deleted })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/content/posts/news/{hidden.Slug}")).StatusCode);
        }

        Assert.Equal([live.Slug], await ListSlugsAsync($"/api/content/posts?type=news&topic={topic.Slug}"));
        string sitemap = await c.GetStringAsync("/api/content/sitemap.xml");
        Assert.Contains($"/tin-tuc/{live.Slug}<", sitemap);
        Assert.DoesNotContain(scheduled.Slug, sitemap);
        Assert.DoesNotContain(draft.Slug, sitemap);

        // Hẹn giờ không cần job: tới giờ là hiện.
        await Sql($"UPDATE content.posts SET published_at = now() - interval '1 second' WHERE id = '{scheduled.Id}'");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/content/posts/news/{scheduled.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Old_slug_redirects_permanently_but_never_to_a_hidden_post()
    {
        Topic topic = await TopicAsync(PostType.News);
        Post live = await PostAsync(PostType.News, topic, "Bài đổi slug");
        Post draft = await PostAsync(PostType.News, topic, "Nháp đổi slug", status: PostStatus.Draft);
        string oldLive = TestDatabase.Unique("cu"), oldDraft = TestDatabase.Unique("cu");
        await Sql($"INSERT INTO content.slug_redirects (post_type, old_slug, post_id) VALUES ('news', '{oldLive}', '{live.Id}'), ('news', '{oldDraft}', '{draft.Id}')");

        HttpClient c = factory.CreateClient(new() { AllowAutoRedirect = false });
        HttpResponseMessage moved = await c.GetAsync($"/api/content/posts/news/{oldLive}");
        Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.Equal($"/api/content/posts/news/{live.Slug}", moved.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/content/posts/news/{oldDraft}")).StatusCode);
    }

    [Fact]
    public async Task List_pages_with_a_stable_cursor_and_rejects_bad_parameters()
    {
        Topic topic = await TopicAsync(PostType.News);
        DateTimeOffset t = DateTimeOffset.UtcNow.AddDays(-1);
        var posts = new List<Post>();
        for (int i = 0; i < 5; i++)
        {
            // Hai bài cùng thời điểm đăng: cursor phải phân biệt bằng id, không mất/trùng bài.
            posts.Add(await PostAsync(PostType.News, topic, $"Bài {i}", publishedAt: t.AddMinutes(i / 2)));
        }

        HttpClient c = factory.CreateClient();
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            using JsonDocument page = JsonDocument.Parse(await c.GetStringAsync(
                $"/api/content/posts?type=news&topic={topic.Slug}&limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")));
            seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("slug").GetString()!));
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(posts.Select(p => p.Slug).ToHashSet(), seen.ToHashSet());
        Assert.Equal(posts[4].Slug, seen[0]); // mới nhất trước

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/content/posts?type=video")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/content/posts?type=news&cursor=not-base64")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/content/posts?type=news&topic={TestDatabase.Unique("khong-co")}")).StatusCode);
        Assert.Equal("public, max-age=60", (await c.GetAsync($"/api/content/posts?type=news&topic={topic.Slug}")).Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Search_without_accents_matches_text_inside_blocks()
    {
        Topic topic = await TopicAsync(PostType.Glossary);
        string word = TestDatabase.Word();
        Post hit = await PostAsync(PostType.Glossary, topic, "Thuật ngữ",
            blocks: $$"""[{"id":"b1","type":"paragraph","spans":[{"text":"Điện toán đám mây {{word}}"}]}]""");
        await PostAsync(PostType.Glossary, topic, "Khác");

        Assert.Equal([hit.Slug], await ListSlugsAsync($"/api/content/posts?type=glossary&q=dien+toan+{word}"));
        Assert.Equal([hit.Slug], await ListSlugsAsync($"/api/content/posts?type=glossary&q=ĐIỆN+{word[..5]}")); // gõ dở, có dấu, hoa
    }

    [Fact]
    public async Task Solution_tree_and_three_level_paths_follow_the_live_urls()
    {
        Topic group = await TopicAsync(PostType.Solution);
        Topic hiddenGroup = await TopicAsync(PostType.Solution, active: false);
        Post page = await PostAsync(PostType.Solution, group, "Trang giải pháp", keyPoints: ["Ý một", "Ý hai"],
            blocks: """[{"id":"b1","type":"paragraph","spans":[{"text":"Mô tả"}]},{"id":"b2","type":"callout","tone":"info","spans":[{"text":"Đối tượng phù hợp: doanh nghiệp vừa"}]}]""");
        Post item = await PostAsync(PostType.Solution, group, "Hạng mục", parent: page);
        Post draftPage = await PostAsync(PostType.Solution, group, "Trang nháp", status: PostStatus.Draft);
        Post orphan = await PostAsync(PostType.Solution, group, "Con của nháp", parent: draftPage);

        HttpClient c = factory.CreateClient();
        using JsonDocument tree = JsonDocument.Parse(await c.GetStringAsync("/api/content/topics?type=solution"));
        JsonElement node = tree.RootElement.EnumerateArray().Single(t => t.GetProperty("slug").GetString() == group.Slug);
        Assert.Equal($"/giai-phap/{group.Slug}", node.GetProperty("path").GetString());
        Assert.Equal([page.Slug], node.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("slug").GetString()));
        JsonElement pageNode = node.GetProperty("pages")[0];
        // Hạng mục con đã đăng đi kèm trang (web dựng menu/thẻ không cần gọi chi tiết); con của trang nháp không lộ.
        Assert.Equal([item.Slug], pageNode.GetProperty("children").EnumerateArray().Select(x => x.GetProperty("slug").GetString()));
        Assert.Equal($"/giai-phap/{group.Slug}/{page.Slug}/{item.Slug}", pageNode.GetProperty("children")[0].GetProperty("path").GetString());
        Assert.Equal(["Ý một", "Ý hai"], pageNode.GetProperty("keyPoints").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Đối tượng phù hợp: doanh nghiệp vừa", pageNode.GetProperty("callout").GetString());
        Assert.DoesNotContain(tree.RootElement.EnumerateArray(), t => t.GetProperty("slug").GetString() == hiddenGroup.Slug);

        using JsonDocument detail = JsonDocument.Parse(await c.GetStringAsync($"/api/content/posts/solution/{item.Slug}"));
        Assert.Equal($"/giai-phap/{group.Slug}/{page.Slug}/{item.Slug}", detail.RootElement.GetProperty("path").GetString());
        Assert.Equal(page.Slug, detail.RootElement.GetProperty("parent").GetProperty("slug").GetString());
        Assert.Equal(group.Slug, detail.RootElement.GetProperty("breadcrumb")[0].GetProperty("slug").GetString());
        Assert.Equal(JsonValueKind.Array, detail.RootElement.GetProperty("blocks").ValueKind);

        using JsonDocument parent = JsonDocument.Parse(await c.GetStringAsync($"/api/content/posts/solution/{page.Slug}"));
        Assert.Equal([item.Slug], parent.RootElement.GetProperty("children").EnumerateArray().Select(x => x.GetProperty("slug").GetString()));

        // Trang cha chưa đăng ⇒ hạng mục con cũng ẩn (cả chi tiết lẫn sitemap).
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/content/posts/solution/{orphan.Slug}")).StatusCode);
        string sitemap = await c.GetStringAsync("/api/content/sitemap.xml");
        Assert.Contains($"https://dxtechs.com/giai-phap/{group.Slug}/{page.Slug}/{item.Slug}", sitemap);
        Assert.DoesNotContain(orphan.Slug, sitemap);
    }

    [Fact]
    public async Task Featured_and_open_course_sessions()
    {
        Topic topic = await TopicAsync(PostType.Training);
        Post course = await PostAsync(PostType.Training, topic, "Khoá nổi bật", featured: true);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        await Sql("INSERT INTO content.course_sessions (id, post_id, starts_on, format, status) VALUES " +
                  $"(gen_random_uuid(), '{course.Id}', '{today.AddDays(10):yyyy-MM-dd}', 'online', 'open'), " +
                  $"(gen_random_uuid(), '{course.Id}', '{today.AddDays(20):yyyy-MM-dd}', 'offline', 'full'), " +
                  $"(gen_random_uuid(), '{course.Id}', '{today.AddDays(30):yyyy-MM-dd}', 'hybrid', 'closed'), " +
                  $"(gen_random_uuid(), '{course.Id}', '{today.AddDays(-5):yyyy-MM-dd}', 'online', 'open')");

        HttpClient c = factory.CreateClient();
        using JsonDocument featured = JsonDocument.Parse(await c.GetStringAsync("/api/content/featured?type=training&limit=24"));
        Assert.Contains(featured.RootElement.EnumerateArray(), x => x.GetProperty("slug").GetString() == course.Slug);

        using JsonDocument sessions = JsonDocument.Parse(await c.GetStringAsync($"/api/content/courses/{course.Slug}/sessions"));
        Assert.Equal(["open", "full"], sessions.RootElement.EnumerateArray().Select(s => s.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/content/courses/{TestDatabase.Unique("x")}/sessions")).StatusCode);
    }

    [Fact]
    public async Task Lead_is_stored_with_consent_version_and_ip_while_bots_get_a_silent_202()
    {
        Topic topic = await TopicAsync(PostType.Training);
        Post course = await PostAsync(PostType.Training, topic, "Khoá có đăng ký");
        string company = TestDatabase.Unique("cty");
        HttpClient c = factory.CreateClient();

        HttpResponseMessage ok = await c.PostAsJsonAsync("/api/content/leads", Lead(company) with { Kind = "course", PostSlug = course.Slug, Phone = "+84 912.345.678" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/content/leads", Lead(company) with { Website = "http://spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/content/leads", Lead(company) with { ElapsedMs = 400 })).StatusCode);

        await using AppDbContext db = TestDatabase.CreateContext();
        Lead stored = await db.Set<Lead>().SingleAsync(l => l.Company == company); // chỉ một: hai lần của bot không được ghi
        Assert.Equal(ApiFactory.TestConsentVersion, stored.ConsentVersion);
        Assert.Equal(LeadKind.Course, stored.Kind);
        Assert.Equal(course.Id, stored.PostId);
        Assert.Equal("0912345678", stored.Phone);
        Assert.Equal("khach@dxtechs.test", stored.Email);
        Assert.Equal(LeadStatus.New, stored.Status);
    }

    [Theory]
    [InlineData("contact")]
    [InlineData("consent")]
    [InlineData("email")]
    [InlineData("postSlug")]
    [InlineData("fullName")]
    public async Task Invalid_lead_is_a_400_with_the_field(string field)
    {
        LeadBody body = Lead("x") with { };
        body = field switch
        {
            "contact" => body with { Email = null, Phone = "  " },
            "consent" => body with { Consent = false },
            "email" => body with { Email = "khach@" },
            "postSlug" => body with { Kind = "course" },
            _ => body with { FullName = new string('a', 101) },
        };

        HttpResponseMessage res = await factory.CreateClient().PostAsJsonAsync("/api/content/leads", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Leads_are_closed_without_an_approved_consent_text_and_rate_limited_per_ip()
    {
        await using var closed = new LeadFactory(consent: "", perHour: 100);
        HttpResponseMessage res = await closed.CreateClient().PostAsJsonAsync("/api/content/leads", Lead("x"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("content.leads_unavailable", await res.Content.ReadAsStringAsync());

        await using var tight = new LeadFactory(consent: "v1", perHour: 2);
        HttpClient c = tight.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/content/leads", Lead("rl"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/content/leads", Lead("rl"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/content/leads", Lead("rl"))).StatusCode);
    }

    [Fact]
    public async Task Sitemap_is_valid_xml_with_static_pages_posts_and_products()
    {
        Topic topic = await TopicAsync(PostType.News);
        Post post = await PostAsync(PostType.News, topic, "Tin cho sitemap");

        HttpResponseMessage res = await factory.CreateClient().GetAsync("/api/content/sitemap.xml");
        Assert.Equal("application/xml", res.Content.Headers.ContentType!.MediaType);
        XDocument xml = XDocument.Parse(await res.Content.ReadAsStringAsync());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        List<string> locs = xml.Descendants(ns + "loc").Select(l => l.Value).ToList();

        Assert.Contains("https://dxtechs.com/", locs);
        Assert.Contains($"https://dxtechs.com/tin-tuc/{post.Slug}", locs);
        Assert.Equal(locs.Count, locs.Distinct().Count());
        Assert.All(locs, l => Assert.StartsWith("https://dxtechs.com/", l));
    }

    // ---------- Hỗ trợ ----------

    public sealed record LeadBody(string? Kind, string? FullName, string? Email, string? Phone, string? Company, string? Message,
        string? PostSlug, Guid? SessionId, bool Consent, string? Website, int? ElapsedMs);

    private static LeadBody Lead(string company)
        => new("consultation", "Nguyễn Khách", "Khach@DXTechs.test", null, company, "Cần tư vấn ERP", null, null, true, null, 8_000);

    private async Task<Topic> TopicAsync(PostType type, bool active = true)
    {
        var topic = new Topic(type, TestDatabase.Unique("chu-de"), "Chủ đề test") { IsActive = active };
        await using AppDbContext db = TestDatabase.CreateContext();
        db.Add(topic);
        await db.SaveChangesAsync();
        return topic;
    }

    private async Task<Post> PostAsync(PostType type, Topic topic, string title, PostStatus status = PostStatus.Published,
        DateTimeOffset? publishedAt = null, bool deleted = false, Post? parent = null, bool featured = false, string? blocks = null,
        List<string>? keyPoints = null)
    {
        var post = new Post(type, TestDatabase.Unique("bai"), title, _author)
        {
            TopicId = topic.Id,
            ParentId = parent?.Id,
            IsFeatured = featured,
            Blocks = blocks ?? """[{"id":"b1","type":"paragraph","spans":[{"text":"Nội dung"}]}]""",
            KeyPoints = keyPoints ?? [],
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        if (status == PostStatus.Published)
        {
            post.Publish(publishedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        else
        {
            post.Status = status;
            post.PublishedAt = publishedAt;
        }

        await using AppDbContext db = TestDatabase.CreateContext();
        db.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    private async Task<List<string>> ListSlugsAsync(string url)
    {
        using JsonDocument doc = JsonDocument.Parse(await factory.CreateClient().GetStringAsync(url));
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("slug").GetString()!).ToList();
    }

    private static async Task Sql(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private sealed class LeadFactory(string consent, int perHour) : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Content:Leads:ConsentVersion", consent);
            builder.UseSetting("Auth:RateLimits:LeadsPerHour", perHour.ToString());
        }
    }
}
