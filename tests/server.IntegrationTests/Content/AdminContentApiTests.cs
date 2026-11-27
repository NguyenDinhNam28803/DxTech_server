using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Content.Domain;
using server.Modules.Content.Jobs;

namespace server.IntegrationTests.Content;

/// <summary>
/// CT-3: API quản trị nội dung (ADR-015). Tiêu chí thiết kế CT mục 5: khối lạ/URL <c>javascript:</c> bị từ chối; editor gọi API đơn hàng ⇒ 403;
/// 409 khi sửa đồng thời; khôi phục phiên bản.
/// </summary>
public class AdminContentApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Password = "Ngựa vằn chạy 42 vòng";
    private string _editor = "";

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        _editor = await TokenAsync("editor");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Only_active_editors_and_admins_reach_the_content_admin_api()
    {
        HttpClient anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/content/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(await TokenAsync("customer")).GetAsync("/api/admin/content/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(_editor).GetAsync("/api/admin/content/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(await TokenAsync("admin")).GetAsync("/api/admin/content/leads")).StatusCode);
        // Editor không thấy dữ liệu bán hàng.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(_editor).GetAsync("/api/admin/payments/unresolved")).StatusCode);
    }

    [Fact]
    public async Task Draft_publish_edit_conflict_and_revision_restore()
    {
        HttpClient c = Client(_editor);
        string slug = TestDatabase.Unique("bai");
        HttpResponseMessage created = await c.PostAsJsonAsync("/api/admin/content/posts", new
        {
            type = "news", slug, content = Input("Bản 1", "Đoạn đầu tiên"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        (Guid id, string etag) = await IdAndETag(created);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/api/content/posts/news/{slug}")).StatusCode); // nháp

        // Thiếu If-Match ⇒ 400; If-Match cũ ⇒ 409.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/admin/content/posts/{id}", Input("x", "y"))).StatusCode);
        HttpResponseMessage v2 = await Send(c, HttpMethod.Put, $"/api/admin/content/posts/{id}", etag, Input("Bản 2", "Đoạn đã sửa"));
        Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Put, $"/api/admin/content/posts/{id}", etag, Input("Ghi đè", "z"))).StatusCode);
        etag = v2.Headers.ETag!.Tag;

        HttpResponseMessage published = await Send(c, HttpMethod.Post, $"/api/admin/content/posts/{id}/publish", etag, new { });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        etag = published.Headers.ETag!.Tag;
        using (JsonDocument pub = JsonDocument.Parse(await factory.CreateClient().GetStringAsync($"/api/content/posts/news/{slug}")))
        {
            Assert.Equal("Bản 2", pub.RootElement.GetProperty("title").GetString());
        }

        using JsonDocument revs = JsonDocument.Parse(await c.GetStringAsync($"/api/admin/content/posts/{id}/revisions"));
        Assert.Equal(["Bản 2", "Bản 1"], revs.RootElement.EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        long first = revs.RootElement[1].GetProperty("id").GetInt64();

        HttpResponseMessage restored = await Send(c, HttpMethod.Post, $"/api/admin/content/posts/{id}/revisions/{first}/restore", etag, new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using JsonDocument after = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        Assert.Equal("Bản 1", after.RootElement.GetProperty("title").GetString());
        Assert.Contains("Đoạn đầu tiên", after.RootElement.GetProperty("blocks").GetRawText());
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>($"/api/admin/content/posts/{id}/revisions")).GetArrayLength()); // khôi phục cũng là một bản
    }

    [Fact]
    public async Task Two_editors_saving_the_same_version_at_once_cannot_both_win()
    {
        HttpClient c = Client(_editor);
        (Guid id, string etag) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts",
            new { type = "news", slug = TestDatabase.Unique("dong-thoi"), content = Input("Gốc", "x") }));

        HttpResponseMessage[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Send(Client(_editor), HttpMethod.Put, $"/api/admin/content/posts/{id}", etag, Input($"Người {i}", "y"))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Theory]
    [InlineData("""[{"id":"b1","type":"cta","label":"Nhấn","href":"javascript:alert(document.cookie)"}]""")]
    [InlineData("""[{"id":"b1","type":"html","html":"<script>alert(1)</script>"}]""")]
    [InlineData("""[{"id":"b1","type":"image","mediaId":"0193a3f0-0000-7000-8000-00000000dead","alt":"Ảnh không tồn tại"}]""")]
    public async Task Unsafe_or_dangling_blocks_are_rejected(string blocks)
    {
        HttpResponseMessage res = await Client(_editor).PostAsJsonAsync("/api/admin/content/posts", new
        {
            type = "news", slug = TestDatabase.Unique("xau"),
            content = new { title = "T", blocks = JsonDocument.Parse(blocks).RootElement, sortOrder = 0, isFeatured = false },
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("blocks", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changing_a_slug_keeps_the_old_url_working_and_changing_back_reclaims_it()
    {
        HttpClient c = Client(_editor);
        string a = TestDatabase.Unique("slug-a"), b = TestDatabase.Unique("slug-b");
        (Guid id, string etag) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "news", slug = a, content = Input("T", "x") }));
        etag = (await Send(c, HttpMethod.Post, $"/api/admin/content/posts/{id}/publish", etag, new { })).Headers.ETag!.Tag;

        HttpResponseMessage renamed = await Send(c, HttpMethod.Put, $"/api/admin/content/posts/{id}/slug", etag, new { slug = b });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        HttpClient web = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.MovedPermanently, (await web.GetAsync($"/api/content/posts/news/{a}")).StatusCode);

        // Slug cũ đang chuyển hướng không được bài mới chiếm.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "news", slug = a, content = Input("T", "x") })).StatusCode);

        // Đổi về slug cũ: URL a lại là trang thật (không vòng chuyển hướng), b chuyển về a.
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Put, $"/api/admin/content/posts/{id}/slug", renamed.Headers.ETag!.Tag, new { slug = a })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync($"/api/content/posts/news/{a}")).StatusCode);
        HttpResponseMessage back = await web.GetAsync($"/api/content/posts/news/{b}");
        Assert.Equal(HttpStatusCode.MovedPermanently, back.StatusCode);
        Assert.EndsWith($"/{a}", back.Headers.Location!.OriginalString);
        await using AppDbContext db = TestDatabase.CreateContext();
        Assert.Equal([b], await db.Set<SlugRedirect>().Where(r => r.PostId == id).Select(r => r.OldSlug).ToListAsync()); // a không còn là "slug cũ"
    }

    [Fact]
    public async Task Only_two_levels_and_pages_with_items_cannot_be_deleted()
    {
        HttpClient c = Client(_editor);
        (Guid page, _) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "solution", slug = TestDatabase.Unique("trang"), content = Input("Trang", "x") }));
        (Guid item, _) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "solution", slug = TestDatabase.Unique("muc"), content = Input("Mục", "x") with { ParentId = page } }));

        HttpResponseMessage third = await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "solution", slug = TestDatabase.Unique("cap3"), content = Input("Cấp 3", "x") with { ParentId = item } });
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);
        Assert.Contains("parentId", await third.Content.ReadAsStringAsync());
        HttpResponseMessage wrongType = await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "news", slug = TestDatabase.Unique("khac-loai"), content = Input("Tin", "x") with { ParentId = page } });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        HttpResponseMessage get = await c.GetAsync($"/api/admin/content/posts/{page}");
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Delete, $"/api/admin/content/posts/{page}", get.Headers.ETag!.Tag, null)).StatusCode);

        HttpResponseMessage getItem = await c.GetAsync($"/api/admin/content/posts/{item}");
        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Delete, $"/api/admin/content/posts/{item}", getItem.Headers.ETag!.Tag, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/admin/content/posts/{item}")).StatusCode);
        await using AppDbContext db = TestDatabase.CreateContext();
        Assert.NotNull((await db.Set<Post>().IgnoreQueryFilters().SingleAsync(p => p.Id == item)).DeletedAt); // xoá mềm
    }

    [Fact]
    public async Task Topics_reject_cycles_and_cannot_be_deleted_while_in_use()
    {
        HttpClient c = Client(_editor);
        JsonElement root = await ReadJson(await c.PostAsJsonAsync("/api/admin/content/topics", Topic(TestDatabase.Unique("goc"), null)));
        Guid rootId = root.GetProperty("id").GetGuid();
        JsonElement child = await ReadJson(await c.PostAsJsonAsync("/api/admin/content/topics", Topic(TestDatabase.Unique("con"), rootId)));

        // Đặt cha của gốc = con của nó ⇒ vòng.
        HttpResponseMessage cycle = await Send(c, HttpMethod.Put, $"/api/admin/content/topics/{rootId}", $"\"{root.GetProperty("version").GetUInt32()}\"",
            Topic(root.GetProperty("slug").GetString()!, child.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/admin/content/topics/{rootId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/content/topics/{child.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/content/topics/{rootId}")).StatusCode);
    }

    [Fact]
    public async Task Course_sessions_and_leads_are_managed_by_editors()
    {
        HttpClient c = Client(_editor);
        string slug = TestDatabase.Unique("khoa");
        (Guid course, string etag) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts", new { type = "training", slug, content = Input("Khoá", "x") }));
        await Send(c, HttpMethod.Post, $"/api/admin/content/posts/{course}/publish", etag, new { });

        string start = DateTime.UtcNow.AddDays(14).ToString("yyyy-MM-dd");
        HttpResponseMessage session = await c.PostAsJsonAsync($"/api/admin/content/courses/{course}/sessions",
            new { startsOn = start, format = "online", seats = 30, priceVnd = 2_500_000 });
        Assert.Equal(HttpStatusCode.Created, session.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/admin/content/courses/{course}/sessions",
            new { startsOn = start, endsOn = "2020-01-01", format = "online" })).StatusCode);
        Assert.Single((await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/content/courses/{slug}/sessions")).EnumerateArray());

        string company = TestDatabase.Unique("cty");
        Assert.Equal(HttpStatusCode.Accepted, (await factory.CreateClient().PostAsJsonAsync("/api/content/leads", new
        {
            kind = "course", fullName = "Học viên", email = "hv@dxtechs.test", company, postSlug = slug, consent = true, elapsedMs = 9000,
        })).StatusCode);

        JsonElement leads = await c.GetFromJsonAsync<JsonElement>("/api/admin/content/leads?status=new&limit=100");
        JsonElement lead = leads.GetProperty("items").EnumerateArray().Single(l => l.GetProperty("company").GetString() == company);
        Assert.Equal("Khoá", lead.GetProperty("postTitle").GetString());
        HttpResponseMessage handled = await c.PutAsJsonAsync($"/api/admin/content/leads/{lead.GetProperty("id").GetGuid()}", new { status = "contacted", note = "Đã gọi" });
        Assert.Equal("contacted", (await ReadJson(handled)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Maintenance_keeps_50_revisions_per_post_and_anonymizes_leads_after_24_months()
    {
        HttpClient c = Client(_editor);
        (Guid id, string etag) = await IdAndETag(await c.PostAsJsonAsync("/api/admin/content/posts",
            new { type = "news", slug = TestDatabase.Unique("nhieu-ban"), content = Input("v0", "x") }));
        await Sql($"INSERT INTO content.post_revisions (post_id, title, blocks, key_points, saved_by) " +
                  $"SELECT '{id}', 'cũ ' || g, '[]', '[]', saved_by FROM content.post_revisions, generate_series(1, 60) g WHERE post_id = '{id}'");
        Guid oldLead = server.Shared.Ids.UuidV7.NewGuid(), newLead = server.Shared.Ids.UuidV7.NewGuid();
        await Sql("INSERT INTO content.leads (id, kind, full_name, email, phone, message, consent_version, consent_at, source_ip, created_at) VALUES " +
                  $"('{oldLead}', 'consultation', 'Cũ', 'cu@x.test', '0912345678', 'nội dung', 'v1', now(), '10.0.0.1', now() - interval '25 months'), " +
                  $"('{newLead}', 'consultation', 'Mới', 'moi@x.test', null, null, 'v1', now(), null, now() - interval '23 months')");

        await factory.Services.GetRequiredService<ContentMaintenance>().RunOnceAsync(default);

        await using AppDbContext db = TestDatabase.CreateContext();
        Assert.Equal(ContentMaintenance.RevisionsKept, await db.Set<PostRevision>().CountAsync(r => r.PostId == id));
        Assert.Equal("cũ 60", (await db.Set<PostRevision>().Where(r => r.PostId == id).OrderByDescending(r => r.Id).FirstAsync()).Title);
        Lead anonymized = await db.Set<Lead>().SingleAsync(l => l.Id == oldLead);
        Assert.Equal((ContentMaintenance.AnonymizedName, (string?)null, (string?)null, (string?)null), (anonymized.FullName, anonymized.Email, anonymized.Phone, anonymized.Message));
        Assert.Null(anonymized.SourceIp);
        Assert.NotNull(anonymized.AnonymizedAt);
        Assert.Equal("moi@x.test", (await db.Set<Lead>().SingleAsync(l => l.Id == newLead)).Email);
    }

    // ---------- Hỗ trợ ----------

    public sealed record PostBody(string Title, JsonElement Blocks, int SortOrder, bool IsFeatured, Guid? ParentId = null);

    private static PostBody Input(string title, string text)
        => new(title, JsonDocument.Parse($$"""[{"id":"b1","type":"paragraph","spans":[{"text":"{{text}}"}]}]""").RootElement, 0, false);

    private static object Topic(string slug, Guid? parentId)
        => new { type = "solution", slug, name = "Chủ đề", parentId, sortOrder = 0, isActive = true };

    private async Task<string> TokenAsync(string role)
    {
        string email = $"{TestDatabase.Unique(role)}@dxtechs.test";
        HttpClient c = factory.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new { email, password = Password, fullName = "Người Test" })).EnsureSuccessStatusCode();
        if (role != "customer")
        {
            await Sql($"UPDATE identity.users SET role = '{role}' WHERE email = '{email}'");
        }

        HttpResponseMessage login = await c.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        return (await ReadJson(login)).GetProperty("accessToken").GetString()!;
    }

    private HttpClient Client(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod method, string url, string etag, object? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
        }

        return c.SendAsync(req);
    }

    private static async Task<(Guid Id, string ETag)> IdAndETag(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return ((await ReadJson(res)).GetProperty("id").GetGuid(), res.Headers.ETag!.Tag);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task Sql(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }
}
