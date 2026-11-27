using System.Text.RegularExpressions;
using server.Modules.Content.Domain;
using server.Modules.Content.Features;
using server.Modules.Content.Features.Admin;
using server.Modules.Content.Features.Public;
using server.Modules.Content.Persistence;
using server.Modules.Content.Seeding;
using server.Modules.Identity.Domain;

namespace server.UnitTests.Content;

/// <summary>CT-1: kiểm khối nội dung (ADR-015 §3), file seed nhúng, role editor, quyền DB khớp roles.sql.</summary>
public class ContentTests
{
    private const string DesignSample = """
        [
          { "id": "b1", "type": "heading", "level": 2, "text": "Bước 1: Kiểm tra thiết bị" },
          { "id": "b2", "type": "paragraph", "spans": [ { "text": "Đối chiếu " }, { "text": "phiên bản Windows", "bold": true },
                                                       { "text": " trên trang", "href": "/san-pham/microsoft-365" } ] },
          { "id": "b3", "type": "list", "ordered": false, "items": [ [ { "text": "Dung lượng trống" } ], [ { "text": "Kết nối Internet" } ] ] },
          { "id": "b4", "type": "image", "mediaId": "0193a3f0-0000-7000-8000-000000000001", "alt": "Màn hình kích hoạt", "caption": null },
          { "id": "b5", "type": "callout", "tone": "warn", "spans": [ { "text": "Không gửi mật khẩu qua chat." } ] },
          { "id": "b6", "type": "cta", "label": "Đăng ký tư vấn", "href": "/lien-he?topic=erp" },
          { "id": "b7", "type": "faq", "question": "Bao lâu thì kích hoạt?", "answer": [ { "text": "Ngay sau khi nhận key." } ] },
          { "id": "b8", "type": "quote", "spans": [ { "text": "Chậm mà chắc.", "italic": true } ], "cite": "Ngạn ngữ" }
        ]
        """;

    [Fact]
    public void Design_sample_is_valid_and_exposes_media_and_plain_text()
    {
        BlocksCheck check = ContentBlocks.Validate(DesignSample);

        Assert.True(check.IsValid, string.Join("; ", check.Errors));
        Assert.Equal([Guid.Parse("0193a3f0-0000-7000-8000-000000000001")], check.MediaIds);
        Assert.Contains("Đối chiếu phiên bản Windows trên trang", check.PlainText);
        Assert.Contains("Bao lâu thì kích hoạt?", check.PlainText);
        Assert.DoesNotContain("/san-pham", check.PlainText);
    }

    [Theory]
    [InlineData("""{"id":"b1","type":"paragraph","spans":[]}""", "$[0].spans")]                                    // đoạn rỗng
    [InlineData("""{"id":"b1","type":"html","html":"<script>alert(1)</script>"}""", "$[0].type")]                  // loại khối lạ
    [InlineData("""{"id":"b1","type":"paragraph","spans":[{"text":"x"}],"style":"color:red"}""", "$[0].style")]     // trường lạ ở khối
    [InlineData("""{"id":"b1","type":"paragraph","spans":[{"text":"x","onclick":"x()"}]}""", "$[0].spans[0].onclick")] // trường lạ ở đoạn chữ
    [InlineData("""{"id":"b1","type":"paragraph","spans":[{"text":"x","href":"javascript:alert(1)"}]}""", "$[0].spans[0].href")]
    [InlineData("""{"id":"b1","type":"cta","label":"Mua","href":"//evil.example/login"}""", "$[0].href")]          // URL không giao thức
    [InlineData("""{"id":"b1","type":"cta","label":"Mua","href":"http://dxtechs.com"}""", "$[0].href")]            // không https
    [InlineData("""{"id":"b1","type":"cta","label":"Mua","href":"https://user:pw@dxtechs.com"}""", "$[0].href")]
    [InlineData("""{"id":"b1","type":"heading","level":1,"text":"H1"}""", "$[0].level")]                           // chỉ H2/H3
    [InlineData("""{"id":"b1","type":"heading","level":2,"text":"   "}""", "$[0].text")]
    [InlineData("""{"id":"b 1","type":"heading","level":2,"text":"x"}""", "$[0].id")]
    [InlineData("""{"id":"b1","type":"image","mediaId":"../../etc/passwd","alt":"x"}""", "$[0].mediaId")]
    [InlineData("""{"id":"b1","type":"image","mediaId":"0193a3f0-0000-7000-8000-000000000001"}""", "$[0].alt")]  // alt bắt buộc
    [InlineData("""{"id":"b1","type":"callout","tone":"danger","spans":[{"text":"x"}]}""", "$[0].tone")]
    [InlineData("""{"id":"b1","type":"paragraph","spans":[{"text":"x","bold":"yes"}]}""", "$[0].spans[0].bold")]
    public void Rejects_anything_outside_the_whitelist(string block, string path)
    {
        BlocksCheck check = ContentBlocks.Validate($"[{block}]");

        Assert.False(check.IsValid);
        Assert.Contains(check.Errors, e => e.StartsWith(path + ":"));
    }

    [Fact]
    public void Rejects_duplicate_ids_non_arrays_too_many_blocks_and_too_much_text()
    {
        Assert.Contains(ContentBlocks.Validate("""[{"id":"a","type":"heading","level":2,"text":"x"},{"id":"a","type":"heading","level":3,"text":"y"}]""").Errors,
            e => e.Contains("trùng id"));
        Assert.False(ContentBlocks.Validate("""{"id":"a"}""").IsValid);
        Assert.False(ContentBlocks.Validate("<p>html</p>").IsValid);

        string many = "[" + string.Join(",", Enumerable.Range(0, ContentBlocks.MaxBlocks + 1)
            .Select(i => $$"""{"id":"b{{i}}","type":"heading","level":2,"text":"x"}""")) + "]";
        Assert.False(ContentBlocks.Validate(many).IsValid);

        string longText = new('a', ContentBlocks.MaxSpanChars);
        string big = "[" + string.Join(",", Enumerable.Range(0, 11)
            .Select(i => $$"""{"id":"b{{i}}","type":"paragraph","spans":[{"text":"{{longText}}"}]}""")) + "]";
        Assert.Contains(ContentBlocks.Validate(big).Errors, e => e.Contains("tổng chữ"));
    }

    [Theory]
    [InlineData("/tin-tuc/abc", true)]
    [InlineData("/", true)]
    [InlineData("https://learn.microsoft.com/vi-vn/", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JAVASCRIPT:alert(1)", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("https://", false)]
    [InlineData("/a b", false)]
    [InlineData("", false)]
    public void Only_internal_paths_and_https_links_are_safe(string href, bool safe)
        => Assert.Equal(safe, ContentBlocks.IsSafeHref(href));

    [Fact]
    public void Reading_minutes_is_about_200_words_per_minute_and_at_least_1()
    {
        Assert.Equal(1, ContentBlocks.ReadingMinutes("một hai ba"));
        Assert.Equal(3, ContentBlocks.ReadingMinutes(string.Join(' ', Enumerable.Repeat("chữ", 401))));
    }

    [Fact]
    public void Embedded_seed_file_is_valid_and_keeps_every_live_slug()
    {
        ContentSeeder.SeedFile data = ContentSeeder.Load();
        var slug = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");

        Assert.Equal(data.Posts.Count, data.Posts.Select(p => (p.PostType, p.Slug)).Distinct().Count());
        Assert.All(data.Posts, p =>
        {
            Assert.Matches(slug, p.Slug);
            Assert.InRange(p.Title.Length, 1, Post.TitleMaxLength);
            Assert.True(p.Excerpt is null || p.Excerpt.Length <= Post.ExcerptMaxLength, p.Slug);
            Assert.True(p.KeyPoints.Count <= Post.MaxKeyPoints, p.Slug);
            BlocksCheck check = ContentBlocks.Validate(p.Blocks.GetRawText());
            Assert.True(check.IsValid, $"{p.PostType}/{p.Slug}: {string.Join("; ", check.Errors)}");
        });

        // Cha đứng trước con (seeder tra cứu theo thứ tự).
        var seenPosts = new HashSet<(string, string)>();
        foreach (ContentSeeder.SeedPost p in data.Posts)
        {
            Assert.True(p.ParentSlug is null || seenPosts.Contains((p.PostType, p.ParentSlug)), p.Slug);
            seenPosts.Add((p.PostType, p.Slug));
        }

        var seenTopics = new HashSet<string>();
        foreach (ContentSeeder.SeedTopic t in data.Topics)
        {
            Assert.True(t.ParentKey is null || seenTopics.Contains(t.ParentKey), t.Key);
            seenTopics.Add(t.Key);
        }

        Assert.All(data.Posts.Where(p => p.TopicKey is not null), p => Assert.Contains(p.TopicKey!, seenTopics));

        // URL đang chạy trên web (mẫu từ mỗi nhóm) — đổi bộ sinh slug sẽ làm test này đỏ.
        string[] live =
        [
            "news/cach-cai-dat-phan-mem-ban-quyen", "news/bao-ve-tai-khoan-so", "news/chon-ten-mien-cho-thuong-hieu",
            "solution/phat-trien-phan-mem", "solution/thiet-ke-ui-ux", "solution/nghien-cuu-va-phat-trien-cong-nghe-r-d",
            "training/ai-cho-doanh-nghiep-x10-hieu-suat", "training/kaizen", "training/tai-chinh-lai-lo",
            "glossary/ci-cd", "glossary/ssl-tls",
        ];
        Assert.All(live, url => Assert.Contains(data.Posts, p => $"{p.PostType}/{p.Slug}" == url));
        Assert.Contains(data.Topics, t => t is { PostType: "solution", Slug: "tu-van-trien-khai", ParentKey: null });
        Assert.Contains(data.Topics, t => t is { PostType: "resource", Slug: "thuat-ngu-cntt" });
    }

    [Fact]
    public void Editor_role_is_stored_and_named_editor()
    {
        Assert.Equal("editor", UserRoles.Name(UserRole.Editor));
        Assert.True(UserRoles.TryParse(" Editor ", out UserRole r));
        Assert.Equal(UserRole.Editor, r);
        Assert.False(UserRoles.TryParse("root", out _));
        Assert.False(UserRoles.TryParse(null, out _));
    }

    [Fact]
    public void Content_privileges_and_functions_match_roles_sql_and_the_migration()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "server.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Không tìm thấy thư mục gốc repo.");
        }

        static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();
        string rolesSql = File.ReadAllText(Path.Combine(root, "deploy", "postgres", "roles.sql"));
        string migration = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "server", "Infrastructure", "Persistence", "Migrations"),
            "*_Content_Initial.cs").Single());

        Assert.Contains(Norm(ContentPrivileges.Sql), Norm(rolesSql));
        Assert.Contains(Norm(ContentSql.Functions), Norm(migration));
    }

    [Theory]
    [InlineData("News", "bao-mat", null, "dich-vu", "/tin-tuc/bao-mat")]
    [InlineData("Solution", "phat-trien-phan-mem", null, "dich-vu", "/giai-phap/dich-vu/phat-trien-phan-mem")]
    [InlineData("Solution", "tich-hop-api", "phat-trien-phan-mem", "dich-vu", "/giai-phap/dich-vu/phat-trien-phan-mem/tich-hop-api")]
    [InlineData("Training", "kaizen", null, "dao-tao", "/giai-phap/dao-tao/kaizen")]
    [InlineData("Page", "chinh-sach-bao-mat", null, null, "/chinh-sach-bao-mat")]
    [InlineData("Glossary", "api", null, null, null)]
    [InlineData("Resource", "clean-code", null, "tai-lieu", null)]
    [InlineData("Solution", "mo-coi", null, null, null)]
    public void Web_paths_match_the_live_router(string type, string slug, string? parent, string? topic, string? expected)
        => Assert.Equal(expected, WebPaths.Post(Enum.Parse<PostType>(type), slug, topic is null ? [] : [topic], parent));

    [Fact]
    public void Topic_paths_cover_solution_training_and_resource_trees()
    {
        Assert.Equal("/giai-phap/dich-vu", WebPaths.Topic(PostType.Solution, ["dich-vu"]));
        Assert.Equal("/tai-nguyen/tai-lieu/sach", WebPaths.Topic(PostType.Resource, ["tai-lieu", "sach"]));
        Assert.Null(WebPaths.Topic(PostType.Glossary, ["x"]));
    }

    [Fact]
    public void Cursor_round_trips_and_rejects_garbage()
    {
        var at = new DateTimeOffset(2026, 11, 27, 6, 0, 0, 123, TimeSpan.Zero);
        Guid id = Guid.NewGuid();
        Assert.True(PublicContentHandler.TryDecodeCursor(PublicContentHandler.EncodeCursor(at, id), out var v));
        Assert.Equal((at, id), v);
        Assert.False(PublicContentHandler.TryDecodeCursor("not-base64!", out _));
        Assert.False(PublicContentHandler.TryDecodeCursor(Convert.ToBase64String("-1|abc"u8.ToArray()), out _));
        Assert.False(PublicContentHandler.TryDecodeCursor(Convert.ToBase64String("123"u8.ToArray()), out _));
    }

    [Fact]
    public void Lead_form_has_its_own_rate_limit_group()
    {
        Assert.Equal("leads", server.Modules.Identity.Auth.AuthSetup.LimitGroup(new Microsoft.AspNetCore.Http.PathString("/api/content/leads")));
        Assert.Null(server.Modules.Identity.Auth.AuthSetup.LimitGroup(new Microsoft.AspNetCore.Http.PathString("/api/content/posts")));
    }

    [Theory]
    [InlineData("\"12345\"", true, 12345u)]
    [InlineData("12345", false, 0u)]
    [InlineData("W/\"12\"", false, 0u)]
    [InlineData("\"-1\"", false, 0u)]
    [InlineData("\"\"", false, 0u)]
    [InlineData(null, false, 0u)]
    public void If_match_accepts_only_the_strong_etag_the_api_sent(string? header, bool ok, uint version)
    {
        Assert.Equal(ok, AdminPostsHandler.TryParseETag(header, out uint v));
        Assert.Equal(version, v);
        if (ok)
        {
            Assert.Equal(header, AdminPostsHandler.ETag(v));
        }
    }
}
