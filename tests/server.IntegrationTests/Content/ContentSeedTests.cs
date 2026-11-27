using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Interceptors;
using server.Modules.Content;
using server.Modules.Content.Domain;
using server.Modules.Content.Persistence;
using server.Modules.Content.Seeding;
using server.Modules.Identity.Contracts;
using server.Modules.Identity.Features.Auth;

namespace server.IntegrationTests.Content;

/// <summary>
/// CT-1: <c>--seed-content</c> (ADR-015 §8) trên database RIÊNG mỗi test (tạo + migrate + xoá) để đếm chính xác.
/// Tiêu chí thiết kế CT mục 5: giữ đúng 100% slug hiện có; chạy 2 lần không nhân đôi.
/// </summary>
public class ContentSeedTests : IAsyncLifetime
{
    private readonly string _database = $"content_{Guid.NewGuid():N}";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ApiFactory.ConnectionString);
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {_database}", admin).ExecuteNonQueryAsync();
        }

        builder.Database = _database;
        _connectionString = builder.ConnectionString;
        await using AppDbContext db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(ApiFactory.ConnectionString);
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Seeds_every_post_with_its_original_slug_once_then_is_idempotent()
    {
        Guid admin = await AdminAsync("first");
        await AdminAsync("second"); // tác giả = admin tạo sớm nhất, không phải admin bất kỳ

        ContentSeedReport first = await Seed();
        ContentSeedReport second = await Seed();

        ContentSeeder.SeedFile file = ContentSeeder.Load();
        Assert.Equal((file.Topics.Count, 0, file.Posts.Count, 0), (first.TopicsCreated, first.TopicsSkipped, first.PostsCreated, first.PostsSkipped));
        Assert.Equal((0, file.Topics.Count, 0, file.Posts.Count), (second.TopicsCreated, second.TopicsSkipped, second.PostsCreated, second.PostsSkipped));

        await using AppDbContext db = Context();
        List<(PostType, string)> stored = (await db.Set<Post>().Select(p => new { p.PostType, p.Slug }).ToListAsync())
            .Select(p => (p.PostType, p.Slug)).ToList();
        Assert.Equal(
            file.Posts.Select(p => (Enum.Parse<PostType>(p.PostType, ignoreCase: true), p.Slug)).Order(),
            stored.Order());
        Assert.All(await db.Set<Post>().ToListAsync(), p =>
        {
            Assert.Equal(PostStatus.Published, p.Status);
            Assert.Equal(admin, p.AuthorId);
            Assert.Equal(TimeSpan.Zero, p.PublishedAt!.Value.Offset);
        });
        Assert.Equal(file.Posts.Count, await db.Set<PostRevision>().CountAsync()); // mỗi bài một điểm khôi phục
    }

    [Fact]
    public async Task Keeps_the_three_level_solution_urls_and_the_news_dates()
    {
        await AdminAsync("a");
        await Seed();
        await using AppDbContext db = Context();

        // /giai-phap/dich-vu/phat-trien-phan-mem/thiet-ke-va-phat-trien-website-web-app
        Topic dichVu = await db.Set<Topic>().SingleAsync(t => t.PostType == PostType.Solution && t.Slug == "dich-vu" && t.ParentId == null);
        Post page = await db.Set<Post>().SingleAsync(p => p.PostType == PostType.Solution && p.Slug == "phat-trien-phan-mem");
        Post item = await db.Set<Post>().SingleAsync(p => p.Slug == "thiet-ke-va-phat-trien-website-web-app");
        Assert.Equal(dichVu.Id, page.TopicId);
        Assert.Null(page.ParentId);
        Assert.Equal(page.Id, item.ParentId);
        Assert.Equal("Thiết kế và phát triển website, Web App", item.Title);

        // Cùng slug ở hai loại khác nhau (giải pháp "Quản lý sản xuất" và khoá đào tạo cùng tên) là hợp lệ.
        Assert.Equal(2, await db.Set<Post>().CountAsync(p => p.Slug == "quan-ly-san-xuat"));
        Post course = await db.Set<Post>().SingleAsync(p => p.PostType == PostType.Training && p.Slug == "ai-cho-doanh-nghiep-x10-hieu-suat");
        Assert.Contains("Tự động hóa công việc và luồng xử lý", course.KeyPoints);

        Post news = await db.Set<Post>().SingleAsync(p => p.PostType == PostType.News && p.Slug == "cach-cai-dat-phan-mem-ban-quyen");
        Assert.Equal(new DateTimeOffset(2025, 8, 12, 2, 0, 0, TimeSpan.Zero), news.PublishedAt); // 09:00 giờ Việt Nam
        Assert.Equal((short)4, news.ReadingMinutes);
        Assert.Equal("HƯỚNG DẪN", news.Label);
        Assert.StartsWith("https://images.unsplash.com/", news.CoverImageUrl);
    }

    [Fact]
    public async Task Search_vector_finds_words_inside_blocks_without_accents()
    {
        await AdminAsync("a");
        await Seed();
        await using AppDbContext db = Context();

        // "Đối chiếu phiên bản Windows" nằm trong một mục danh sách của bài hướng dẫn cài đặt, không ở tiêu đề/tóm tắt.
        List<string> hits = await db.Set<Post>()
            .Where(p => EF.Property<NpgsqlTypes.NpgsqlTsVector>(p, PostConfiguration.SearchVector)
                .Matches(EF.Functions.PlainToTsQuery("simple", "doi chieu phien ban windows")))
            .Select(p => p.Slug).ToListAsync();

        Assert.Equal(["cach-cai-dat-phan-mem-ban-quyen"], hits);
    }

    [Fact]
    public async Task Re_seeding_never_overwrites_editor_changes_or_resurrects_deleted_posts()
    {
        await AdminAsync("a");
        await Seed();
        await using (AppDbContext db = Context())
        {
            Post news = await db.Set<Post>().SingleAsync(p => p.Slug == "bao-ve-tai-khoan-so");
            news.Title = "Tiêu đề do biên tập viên sửa";
            Post glossary = await db.Set<Post>().SingleAsync(p => p.PostType == PostType.Glossary && p.Slug == "api");
            glossary.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        ContentSeedReport again = await Seed();

        Assert.Equal(0, again.PostsCreated);
        await using AppDbContext check = Context();
        Assert.Equal("Tiêu đề do biên tập viên sửa", (await check.Set<Post>().SingleAsync(p => p.Slug == "bao-ve-tai-khoan-so")).Title);
        Assert.False(await check.Set<Post>().AnyAsync(p => p.PostType == PostType.Glossary && p.Slug == "api"));
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_missing_admin_fails_with_exit_code_1()
    {
        var output = new StringWriter();
        Assert.Equal(1, await ContentModule.SeedContentAsync(Services(), dryRun: false, output));
        Assert.Contains("--create-admin", output.ToString());

        await AdminAsync("a");
        output = new StringWriter();
        Assert.Equal(0, await ContentModule.SeedContentAsync(Services(), dryRun: true, output));
        Assert.Matches(new Regex(@"DRY RUN.*bài \+\d+"), output.ToString());

        await using AppDbContext db = Context();
        Assert.Equal(0, await db.Set<Post>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Set<Topic>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Database_rejects_what_the_code_would_never_write()
    {
        Guid admin = await AdminAsync("a");
        string insert(string slug, string blocks, string status = "draft", string publishedAt = "NULL", string type = "news") =>
            $"INSERT INTO content.posts (id, post_type, slug, title, blocks, status, published_at, author_id, updated_by, sort_order, is_featured) " +
            $"VALUES (gen_random_uuid(), '{type}', '{slug}', 'T', '{blocks}', '{status}', {publishedAt}, '{admin}', '{admin}', 0, false)";

        await Sql(insert("hop-le", "[]"));
        await AssertCheck(insert("Sai Slug", "[]"), "ck_posts_slug");
        await AssertCheck(insert("khoi-object", "{}"), "ck_posts_blocks");
        await AssertCheck(insert("dang-khong-ngay", "[]", status: "published"), "ck_posts_published_at");
        await AssertCheck(insert("loai-la", "[]", type: "video"), "ck_posts_post_type");
        await AssertUnique(insert("hop-le", "[]"));                   // trùng (loại, slug)
        await Sql(insert("hop-le", "[]", type: "glossary"));          // cùng slug, khác loại: được

        await AssertCheck("INSERT INTO content.leads (id, kind, full_name, consent_version, consent_at) " +
                          "VALUES (gen_random_uuid(), 'consultation', 'Khách', 'v1', now())", "ck_leads_contact");
        await AssertCheck($"UPDATE identity.users SET role = 'superadmin' WHERE id = '{admin}'", "ck_users_role");
        await Sql($"UPDATE identity.users SET role = 'editor' WHERE id = '{admin}'");
    }

    // ---------- Hỗ trợ ----------

    private async Task<Guid> AdminAsync(string name)
    {
        Guid id = server.Shared.Ids.UuidV7.NewGuid();
        await Sql("INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp) " +
                  $"VALUES ('{id}', '{name}-{id:N}@dxtechs.test', repeat('h', 40), 'Admin {name}', 'admin', 'active', 0, gen_random_uuid())");
        await Task.Delay(5); // created_at khác nhau ⇒ thứ tự "admin tạo sớm nhất" xác định
        return id;
    }

    private async Task<ContentSeedReport> Seed(bool dryRun = false)
    {
        await using AsyncServiceScope scope = Services().CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ContentSeeder>().RunAsync(dryRun, default);
    }

    private async Task Sql(string sql)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private async Task AssertCheck(string sql, string constraint)
    {
        PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => Sql(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
    }

    private async Task AssertUnique(string sql)
    {
        PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => Sql(sql));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    private AppDbContext Context()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(new AuditableInterceptor(TimeProvider.System))
            .Options);

    private IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_connectionString)
            .AddInterceptors(new AuditableInterceptor(TimeProvider.System)));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IUserProfiles>(sp => new UserProfiles(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<ContentSeeder>();
        return services.BuildServiceProvider();
    }
}
