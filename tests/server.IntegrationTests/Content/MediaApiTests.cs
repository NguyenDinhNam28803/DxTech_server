using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using server.IntegrationTests.Infrastructure;

namespace server.IntegrationTests.Content;

/// <summary>
/// CT-3b: thư viện ảnh (ADR-015 §5). Tiêu chí thiết kế CT mục 5: SVG/ảnh đổi đuôi bị từ chối; ảnh có GPS ⇒ lưu ra không còn GPS.
/// </summary>
public class MediaApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Password = "Ngựa vằn chạy 42 vòng";
    private static readonly byte[] Gps = Encoding.ASCII.GetBytes("GPS-10.7769N-106.7009E");
    private string _editor = "";

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        _editor = await TokenAsync("editor");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Upload_strips_gps_stores_under_year_month_and_serves_the_url_in_post_details()
    {
        byte[] jpeg = Jpeg(1200, 800, Unique());
        HttpResponseMessage res = await Upload(Client(_editor), jpeg, "anh.png", "image/png", alt: "Màn hình kích hoạt"); // đuôi/Content-Type sai cũng không sao

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        JsonElement m = await ReadJson(res);
        Assert.Equal(("image/jpeg", 1200, 800), (m.GetProperty("contentType").GetString(), m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32()));
        string url = m.GetProperty("url").GetString()!;
        Assert.Matches(@"^/media/\d{4}/\d{2}/[0-9a-f]{32}\.jpg$", url);
        byte[] stored = await File.ReadAllBytesAsync(Path.Combine(ApiFactory.MediaRoot, url["/media/".Length..]));
        Assert.True(stored.AsSpan().IndexOf(Gps) < 0, "file lưu trên đĩa còn GPS");
        Assert.Empty(Directory.GetFiles(ApiFactory.MediaRoot, ".upload-*")); // không còn file tạm

        // Dùng ảnh trong bài ⇒ API công khai trả URL + kích thước để web dựng <img>.
        Guid mediaId = m.GetProperty("id").GetGuid();
        string slug = TestDatabase.Unique("co-anh");
        string blocks = $$"""[{"id":"b1","type":"image","mediaId":"{{mediaId}}","alt":"Ảnh"}]""";
        HttpResponseMessage created = await Client(_editor).PostAsJsonAsync("/api/admin/content/posts", new
        {
            type = "news", slug, content = new { title = "Có ảnh", blocks = JsonDocument.Parse(blocks).RootElement, coverMediaId = mediaId, sortOrder = 0, isFeatured = false },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var publish = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/content/posts/{(await ReadJson(created)).GetProperty("id").GetGuid()}/publish")
        {
            Content = JsonContent.Create(new { }),
        };
        publish.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, (await Client(_editor).SendAsync(publish)).StatusCode);

        JsonElement detail = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/content/posts/news/{slug}");
        Assert.Equal(url, detail.GetProperty("media").GetProperty(mediaId.ToString()).GetProperty("url").GetString());
        Assert.Equal(url, detail.GetProperty("cover").GetProperty("url").GetString());

        // Ảnh đang dùng không xoá được.
        Assert.Equal(HttpStatusCode.Conflict, (await Client(_editor).DeleteAsync($"/api/admin/content/media/{mediaId}")).StatusCode);
    }

    [Fact]
    public async Task Same_image_twice_returns_the_existing_one_and_a_deleted_one_is_restored()
    {
        byte[] png = Png(64, 64, Unique());
        JsonElement first = await ReadJson(await Upload(Client(_editor), png, "a.png", "image/png"));
        HttpResponseMessage again = await Upload(Client(_editor), png, "khac-ten.png", "image/png");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        JsonElement second = await ReadJson(again);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.True(second.GetProperty("duplicate").GetBoolean());

        Guid id = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Client(_editor).DeleteAsync($"/api/admin/content/media/{id}")).StatusCode);
        JsonElement list = await Client(_editor).GetFromJsonAsync<JsonElement>("/api/admin/content/media?limit=100");
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);

        HttpResponseMessage restored = await Upload(Client(_editor), png, "a.png", "image/png");
        Assert.Equal(id, (await ReadJson(restored)).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("html-as-jpg")]
    [InlineData("too-wide")]
    [InlineData("too-large")]
    [InlineData("no-file")]
    public async Task Rejects_non_images_oversized_files_and_missing_files(string kind)
    {
        HttpResponseMessage res = kind switch
        {
            "svg" => await Upload(Client(_editor), "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray(), "a.svg", "image/svg+xml"),
            "html-as-jpg" => await Upload(Client(_editor), "<html><script>alert(1)</script></html>"u8.ToArray(), "a.jpg", "image/jpeg"),
            "too-wide" => await Upload(Client(_editor), Jpeg(6001, 100, Unique()), "a.jpg", "image/jpeg"),
            "too-large" => await Upload(Client(_editor), [.. Jpeg(100, 100, Unique()), .. new byte[5 * 1024 * 1024]], "a.jpg", "image/jpeg"),
            _ => await Client(_editor).PostAsync("/api/admin/content/media", new MultipartFormDataContent { { new StringContent("x"), "alt" } }),
        };

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("content.invalid_media", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Customers_cannot_upload_and_an_unwritable_media_folder_only_breaks_uploads()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(Client(await TokenAsync("customer")), Png(8, 8, Unique()), "a.png", "image/png")).StatusCode);

        await using var broken = new BrokenMediaFactory();
        HttpClient c = broken.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _editor);
        HttpResponseMessage res = await Upload(c, Png(9, 9, Unique()), "a.png", "image/png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("content.media_unavailable", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/content/posts?type=news")).StatusCode);
    }

    // ---------- Ảnh thử (đúng cấu trúc định dạng, có EXIF chứa GPS) ----------

    private static byte[] Unique() => Guid.NewGuid().ToByteArray();

    private static byte[] Jpeg(int width, int height, byte[] salt)
    {
        var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);
        Seg(s, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. Gps]);
        Seg(s, 0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        Seg(s, 0xDA, [1, 1, 0, 0, 0x3F, 0]);
        s.Write(salt); // dữ liệu nén khác nhau ⇒ SHA-256 khác nhau giữa các test
        s.Write([0xFF, 0xD9]);
        return s.ToArray();

        static void Seg(MemoryStream s, byte marker, byte[] p)
        {
            s.Write([0xFF, marker, (byte)((p.Length + 2) >> 8), (byte)(p.Length + 2)]);
            s.Write(p);
        }
    }

    private static byte[] Png(int width, int height, byte[] salt)
    {
        var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        Chunk(s, "IHDR", ihdr);
        Chunk(s, "tEXt", Gps);
        Chunk(s, "IDAT", salt);
        Chunk(s, "IEND", []);
        return s.ToArray();

        static void Chunk(MemoryStream s, string type, byte[] d)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)d.Length);
            s.Write(len);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(d);
            s.Write([0, 0, 0, 0]);
        }
    }

    // ---------- Hỗ trợ ----------

    private static Task<HttpResponseMessage> Upload(HttpClient c, byte[] bytes, string fileName, string contentType, string? alt = null)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        if (alt is not null)
        {
            form.Add(new StringContent(alt), "alt");
        }

        return c.PostAsync("/api/admin/content/media", form);
    }

    private async Task<string> TokenAsync(string role)
    {
        string email = $"{TestDatabase.Unique(role)}@dxtechs.test";
        HttpClient c = factory.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new { email, password = Password, fullName = "Người Test" })).EnsureSuccessStatusCode();
        if (role != "customer")
        {
            await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
            await conn.OpenAsync();
            await new NpgsqlCommand($"UPDATE identity.users SET role = '{role}' WHERE email = '{email}'", conn).ExecuteNonQueryAsync();
        }

        return (await ReadJson(await c.PostAsJsonAsync("/api/auth/login", new { email, password = Password }))).GetProperty("accessToken").GetString()!;
    }

    private HttpClient Client(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>Thư mục ảnh trỏ vào bên trong một FILE ⇒ không bao giờ tạo được thư mục (giống thư mục thuộc root trên VPS).</summary>
    private sealed class BrokenMediaFactory : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            string file = Path.GetTempFileName();
            builder.UseSetting("Content:MediaRoot", Path.Combine(file, "media"));
        }
    }
}
