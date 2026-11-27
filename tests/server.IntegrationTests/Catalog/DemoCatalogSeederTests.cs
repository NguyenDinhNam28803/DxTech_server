using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Interceptors;
using server.Modules.Catalog;
using server.Modules.Catalog.Domain;
using server.Modules.Catalog.Features.Products;
using server.Modules.Catalog.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace server.IntegrationTests.Catalog;

/// <summary>
/// PR-5: seeder dữ liệu demo (ADR-010). Mỗi test dùng một database RIÊNG (tạo + migrate + xoá) để đếm chính xác
/// và không phụ thuộc dữ liệu của test khác.
/// </summary>
public class DemoCatalogSeederTests : IAsyncLifetime
{
    private readonly string _database = $"seed_{Guid.NewGuid():N}";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ApiFactory.ConnectionString);
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
            await cmd.ExecuteNonQueryAsync();
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
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Seeds_everything_once_then_is_idempotent()
    {
        SeedReport first = await Seed();
        SeedReport second = await Seed();

        Assert.Equal(new SeedReport(16, 0, 16, 0, 16, false), first);
        Assert.Equal(new SeedReport(0, 16, 0, 16, 0, false), second);

        await using AppDbContext db = Context();
        Assert.Equal(6 + 16, await db.Set<Category>().CountAsync());
        Assert.Equal(16, await db.Set<Product>().CountAsync());
        Assert.Equal(16, await db.Set<ProductVariant>().CountAsync(v => v.IsDefault));
    }

    [Fact]
    public async Task Dry_run_reports_counts_but_writes_nothing()
    {
        SeedReport report = await Seed(dryRun: true);

        Assert.Equal(16, report.ProductsCreated);
        await using AppDbContext db = Context();
        Assert.Equal(0, await db.Set<Product>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(6, await db.Set<Category>().CountAsync()); // chỉ còn 6 danh mục gốc của migration
    }

    [Fact]
    public async Task Re_seeding_never_overwrites_admin_edits_or_resurrects_deleted_products()
    {
        await Seed();
        await using (AppDbContext db = Context())
        {
            ProductVariant windows = await db.Set<ProductVariant>().SingleAsync(v => v.Sku == "WINDOWS-11-PRO-RETAIL");
            windows.PriceVnd = 590_000; // admin giảm giá
            Product canva = await db.Set<Product>().SingleAsync(p => p.Slug == "canva-pro-edu-business");
            canva.DeletedAt = DateTimeOffset.UtcNow; // admin gỡ sản phẩm
            await db.SaveChangesAsync();
        }

        SeedReport again = await Seed();

        Assert.Equal(0, again.ProductsCreated);
        await using AppDbContext check = Context();
        Assert.Equal(590_000, (await check.Set<ProductVariant>().SingleAsync(v => v.Sku == "WINDOWS-11-PRO-RETAIL")).PriceVnd);
        Assert.Equal(1, await check.Set<Product>().IgnoreQueryFilters().CountAsync(p => p.Slug == "canva-pro-edu-business"));
        Assert.False(await check.Set<Product>().AnyAsync(p => p.Slug == "canva-pro-edu-business"));
    }

    [Fact]
    public async Task Failure_rolls_back_everything_and_returns_exit_code_1()
    {
        await using (AppDbContext db = Context())
        {
            // Mất danh mục gốc "cloud" ⇒ danh mục con "cloud-vps" không có cha ⇒ seeder phải dừng.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.categories WHERE slug = 'cloud'");
        }

        var output = new StringWriter();
        int exitCode = await CatalogModule.SeedDemoAsync(Services(), dryRun: false, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("THẤT BẠI", output.ToString());
        Assert.Contains("cloud", output.ToString());
        await using AppDbContext check = Context();
        Assert.Equal(5, await check.Set<Category>().CountAsync());          // không có danh mục con nào được ghi
        Assert.Equal(0, await check.Set<Product>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Command_prints_a_summary_and_returns_exit_code_0()
    {
        var output = new StringWriter();

        int exitCode = await CatalogModule.SeedDemoAsync(Services(), dryRun: true, output);

        Assert.Equal(0, exitCode);
        Assert.Matches(new Regex(@"DRY RUN.*sản phẩm \+16"), output.ToString());
    }

    [Fact]
    public async Task Seeded_catalog_is_served_by_the_api_handlers()
    {
        await Seed();
        await using AppDbContext db = Context();

        var software = await new ListProductsHandler(db).HandleAsync(new ListProductsQuery { Category = "software", PageSize = 50 }, default);
        var design = await new ListProductsHandler(db).HandleAsync(new ListProductsQuery { Category = "thiet-ke" }, default);
        var search = await new ListProductsHandler(db).HandleAsync(new ListProductsQuery { Q = "kaspersky" }, default);
        var windows = await new GetProductBySlugHandler(db, new server.Modules.Inventory.Features.StockQuery(db)).HandleAsync("windows-11-pro-retail", default);

        Assert.Equal(10, software.Value.Total);           // 10 sản phẩm phần mềm, qua các danh mục con
        Assert.Equal(3, design.Value.Total);              // Adobe, Figma, Canva
        Assert.Equal("kaspersky-total-security-2025", Assert.Single(search.Value.Items).Slug);
        Assert.Equal(["software", "he-dieu-hanh"], windows.Value.Breadcrumb.Select(b => b.Slug));
        Assert.Equal("perpetual", windows.Value.LicenseType);
        ProductVariantItem variant = Assert.Single(windows.Value.Variants);
        Assert.Null(variant.TermMonths);
        Assert.Equal(690_000, variant.PriceVnd);
        Assert.Equal(1_490_000, variant.CompareAtPriceVnd);
    }

    [Fact]
    public void Embedded_seed_file_is_consistent()
    {
        DemoCatalogSeeder.DemoCatalog data = DemoCatalogSeeder.Load();

        Assert.Equal(data.Products.Count, data.Products.Select(p => p.Slug).Distinct().Count());
        Assert.Equal(data.Products.Count, data.Products.SelectMany(p => p.Variants).Select(v => v.Sku).Distinct().Count());
        Assert.All(data.Products, p => Assert.Contains(data.Categories, c => c.Slug == p.CategorySlug));
        Assert.All(data.Products, p => Assert.Single(p.Variants, v => v.IsDefault));
        Assert.DoesNotContain(data.Products, p => p.Slug == "search"); // đoạn route dành riêng
    }

    private async Task<SeedReport> Seed(bool dryRun = false)
    {
        await using AppDbContext db = Context();
        return await new DemoCatalogSeeder(db).RunAsync(dryRun, default);
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
        services.AddScoped<DemoCatalogSeeder>();
        return services.BuildServiceProvider();
    }
}
