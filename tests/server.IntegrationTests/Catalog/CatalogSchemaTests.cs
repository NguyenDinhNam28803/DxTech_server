using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Modules.Catalog.Persistence;
using server.Shared.Ids;

namespace server.IntegrationTests.Catalog;

/// <summary>
/// Migration Catalog_Initial trên PostgreSQL thật (ADR-005): ràng buộc, index, tìm kiếm không dấu,
/// xoá mềm, concurrency token xmin. Không mock — đúng những gì production sẽ chạy.
/// </summary>
public class CatalogSchemaTests : IAsyncLifetime
{
    private static readonly Guid SoftwareCategoryId = Guid.Parse("01a1fbe1-6754-7541-941d-ea90e3341d07");

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Dữ liệu cấu hình & đối tượng DB ----------

    [Fact]
    public async Task Migration_seeds_the_six_root_categories_used_by_the_client()
    {
        await using AppDbContext db = TestDatabase.CreateContext();

        List<string> slugs = await db.Set<Category>()
            .Where(c => c.ParentId == null && c.Id.ToString().StartsWith("01a1fbe1-67"))
            .OrderBy(c => c.SortOrder)
            .Select(c => c.Slug)
            .ToListAsync();

        Assert.Equal(["software", "cloud", "domain", "ssl", "storage", "backup"], slugs);
    }

    [Fact]
    public async Task Unaccent_wrapper_is_immutable_and_strips_vietnamese_marks()
    {
        await using AppDbContext db = TestDatabase.CreateContext();

        string result = await Scalar<string>(db, "SELECT catalog.f_unaccent('Phần mềm ĐỒ HỌA Đà Nẵng')");
        string volatility = await Scalar<string>(db, "SELECT provolatile::text FROM pg_proc WHERE oid = 'catalog.f_unaccent(text)'::regprocedure");

        Assert.Equal("Phan mem DO HOA Da Nang", result);
        Assert.Equal("i", volatility); // IMMUTABLE — điều kiện để dùng trong generated column và index
    }

    [Theory]
    [InlineData("products", "ix_products_search_vector", "gin")]
    [InlineData("products", "ix_products_platforms", "gin")]
    [InlineData("products", "ix_products_slug", "btree")]
    [InlineData("product_variants", "ux_product_variants_one_default", "btree")]
    [InlineData("product_variants", "ix_product_variants_product_id_price_vnd", "btree")]
    public async Task Indexes_designed_for_real_queries_exist(string table, string index, string method)
    {
        await using AppDbContext db = TestDatabase.CreateContext();

        string? found = await Scalar<string?>(db, $"""
            SELECT am.amname FROM pg_class i
              JOIN pg_index x ON x.indexrelid = i.oid
              JOIN pg_class t ON t.oid = x.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_am am ON am.oid = i.relam
             WHERE n.nspname = 'catalog' AND t.relname = '{table}' AND i.relname = '{index}'
            """);

        Assert.Equal(method, found);
    }

    // ---------- Ghi/đọc qua EF ----------

    [Fact]
    public async Task Product_with_variants_round_trips_through_ef()
    {
        Product product = NewProduct(out ProductVariant variant);
        product.Platforms = [Platforms.Windows, Platforms.MacOs];
        product.Features = ["Kích hoạt online", "Bảo hành trọn đời"];
        variant.TermMonths = 12;
        variant.Seats = 6;
        variant.CompareAtPriceVnd = 1_990_000;

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.Add(product);
            await db.SaveChangesAsync();
        }

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            Product loaded = await db.Set<Product>().Include(p => p.Variants).Include(p => p.Category)
                .SingleAsync(p => p.Id == product.Id);

            Assert.Equal("software", loaded.Category!.Slug);
            Assert.Equal(LicenseType.Subscription, loaded.LicenseType);
            Assert.Equal([Platforms.Windows, Platforms.MacOs], loaded.Platforms);
            Assert.Equal(["Kích hoạt online", "Bảo hành trọn đời"], loaded.Features);
            Assert.Equal(7, UuidV7.GetVersion(loaded.Id));
            Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);

            ProductVariant v = Assert.Single(loaded.Variants);
            Assert.Equal(1_350_000, v.PriceVnd);
            Assert.Equal(DeliveryType.LicenseKey, v.DeliveryType);
            Assert.NotEqual(0u, v.Version);
        }
    }

    [Fact]
    public async Task Values_equal_to_clr_defaults_are_not_replaced_by_database_defaults()
    {
        // Hồi quy: HasDefaultValue(true) không có sentinel ⇒ EF bỏ is_active=false khỏi INSERT ⇒ DB ghi true.
        Product product = NewProduct(out ProductVariant variant);
        product.IsActive = false;
        variant.IsActive = false;
        var category = new Category(TestDatabase.Unique("hidden"), "Ẩn") { IsActive = false };

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.AddRange(product, category);
            await db.SaveChangesAsync();
        }

        await using AppDbContext check = TestDatabase.CreateContext();
        Assert.False((await check.Set<Product>().SingleAsync(p => p.Id == product.Id)).IsActive);
        Assert.False((await check.Set<ProductVariant>().SingleAsync(v => v.Id == variant.Id)).IsActive);
        Assert.False((await check.Set<Category>().SingleAsync(c => c.Id == category.Id)).IsActive);
        Assert.Equal(1, (await check.Set<ProductVariant>().SingleAsync(v => v.Id == variant.Id)).Seats);
    }

    [Fact]
    public async Task Enums_are_stored_as_readable_snake_case_text()
    {
        Product product = NewProduct(out ProductVariant variant);
        await Save(product);

        await using AppDbContext db = TestDatabase.CreateContext();
        string license = await Scalar<string>(db, $"SELECT license_type FROM catalog.products WHERE id = '{product.Id}'");
        string delivery = await Scalar<string>(db, $"SELECT delivery_type FROM catalog.product_variants WHERE id = '{variant.Id}'");

        Assert.Equal("subscription", license);
        Assert.Equal("license_key", delivery);
    }

    [Fact]
    public async Task Features_are_stored_as_a_jsonb_array()
    {
        Product product = NewProduct(out _);
        product.Features = ["A", "B"];
        await Save(product);

        await using AppDbContext db = TestDatabase.CreateContext();
        string json = await Scalar<string>(db, $"SELECT features::text FROM catalog.products WHERE id = '{product.Id}'");

        Assert.Equal("[\"A\", \"B\"]", json);
    }

    // ---------- Tìm kiếm không dấu ----------

    [Theory]
    [InlineData("phan mem do hoa")]      // gõ không dấu
    [InlineData("PHẦN MỀM ĐỒ HOẠ")]      // có dấu, chữ hoa
    [InlineData("adobe")]                // theo hãng
    [InlineData("thiet ke")]             // theo mô tả ngắn
    public async Task Search_vector_matches_without_vietnamese_marks(string query)
    {
        string marker = TestDatabase.Word();
        Product product = NewProduct(out _);
        product.Name = $"Phần mềm Đồ hoạ {marker}";
        product.Brand = "Adobe";
        product.ShortDescription = "Bộ công cụ thiết kế chuyên nghiệp";
        await Save(product);

        await using AppDbContext db = TestDatabase.CreateContext();
        string tsQuery = string.Join(" & ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(marker));

        List<Guid> ids = await db.Set<Product>()
            .Where(p => EF.Property<NpgsqlTsVector>(p, ProductConfiguration.SearchVector)
                .Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            .Select(p => p.Id)
            .ToListAsync();

        // Câu truy vấn tìm kiếm cũng phải bỏ dấu (PR-4 làm ở handler) — ở đây kiểm tra cả hai phía bằng f_unaccent.
        List<Guid> idsUnaccented = await db.Database
            .SqlQueryRaw<Guid>(
                "SELECT id AS \"Value\" FROM catalog.products WHERE search_vector @@ to_tsquery('simple', catalog.f_unaccent({0}))",
                tsQuery)
            .ToListAsync();

        Assert.Contains(product.Id, idsUnaccented);
        if (query == "phan mem do hoa" || query == "adobe" || query == "thiet ke")
        {
            Assert.Contains(product.Id, ids);
        }
    }

    [Fact]
    public async Task Search_vector_is_recomputed_when_the_name_changes()
    {
        Product product = NewProduct(out _);
        await Save(product);

        string marker = TestDatabase.Word();
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            Product loaded = await db.Set<Product>().SingleAsync(p => p.Id == product.Id);
            loaded.Name = $"Tên mới {marker}";
            await db.SaveChangesAsync();
        }

        await using AppDbContext check = TestDatabase.CreateContext();
        bool found = await check.Set<Product>().AnyAsync(p => p.Id == product.Id
            && EF.Property<NpgsqlTsVector>(p, ProductConfiguration.SearchVector).Matches(EF.Functions.ToTsQuery("simple", $"ten & moi & {marker}")));
        Assert.True(found);
    }

    // ---------- Xoá mềm ----------

    [Fact]
    public async Task Soft_deleted_rows_are_hidden_and_free_their_slug_and_sku()
    {
        Product first = NewProduct(out ProductVariant firstVariant);
        await Save(first);

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            Product loaded = await db.Set<Product>().Include(p => p.Variants).SingleAsync(p => p.Id == first.Id);
            loaded.DeletedAt = DateTimeOffset.UtcNow;
            loaded.Variants.Single().DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            Assert.False(await db.Set<Product>().AnyAsync(p => p.Id == first.Id));
            Assert.True(await db.Set<Product>().IgnoreQueryFilters().AnyAsync(p => p.Id == first.Id));
        }

        // Slug/SKU của bản đã xoá dùng lại được (unique index có WHERE deleted_at IS NULL).
        Product second = NewProduct(out ProductVariant secondVariant, slug: first.Slug, sku: firstVariant.Sku);
        await Save(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(firstVariant.Sku, secondVariant.Sku);
    }

    // ---------- Ràng buộc (CHECK, UNIQUE) ----------

    [Fact]
    public async Task Duplicate_active_slug_is_rejected()
    {
        Product first = NewProduct(out _);
        await Save(first);

        Product duplicate = NewProduct(out _, slug: first.Slug);
        await AssertViolates(duplicate, PostgresErrorCodes.UniqueViolation, "ix_products_slug");
    }

    [Fact]
    public async Task Only_one_default_variant_per_product()
    {
        Product product = NewProduct(out ProductVariant first);
        first.IsDefault = true;
        var second = new ProductVariant(product.Id, TestDatabase.Unique("SKU"), "Gói 2", 2_000_000, DeliveryType.Account) { IsDefault = true };
        product.Variants.Add(second);

        await AssertViolates(product, PostgresErrorCodes.UniqueViolation, "ux_product_variants_one_default");
    }

    // Theory nhận tên trường hợp (string) vì entity là internal — xUnit yêu cầu tham số test public.
    private static readonly Dictionary<string, (string Constraint, Action<Product, ProductVariant> Break)> InvalidCases = new()
    {
        ["slug có chữ hoa và dấu cách"] = ("ck_products_slug", (p, _) => p.Slug = "Windows 11"),
        ["slug bắt đầu bằng gạch ngang"] = ("ck_products_slug", (p, _) => p.Slug = "-leading-dash"),
        ["tên rỗng"] = ("ck_products_name", (p, _) => p.Name = ""),
        ["tên quá 200 ký tự"] = ("ck_products_name", (p, _) => p.Name = new string('x', 201)),
        ["nền tảng ngoài danh sách"] = ("ck_products_platforms", (p, _) => p.Platforms = ["Windows"]),
        ["giá âm"] = ("ck_product_variants_price", (_, v) => v.PriceVnd = -1),
        ["giá gốc không lớn hơn giá bán"] = ("ck_product_variants_compare_at_price", (_, v) => v.CompareAtPriceVnd = v.PriceVnd),
        ["thời hạn 0 tháng"] = ("ck_product_variants_term_months", (_, v) => v.TermMonths = 0),
        ["0 người dùng"] = ("ck_product_variants_seats", (_, v) => v.Seats = 0),
        ["sku có dấu cách"] = ("ck_product_variants_sku", (_, v) => v.Sku = "has space"),
    };

    public static TheoryData<string> InvalidCaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in InvalidCases.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCaseNames))]
    public async Task Check_constraints_reject_invalid_data(string caseName)
    {
        (string constraint, Action<Product, ProductVariant> breakIt) = InvalidCases[caseName];
        Product product = NewProduct(out ProductVariant variant);
        breakIt(product, variant);

        await AssertViolates(product, PostgresErrorCodes.CheckViolation, constraint);
    }

    [Fact]
    public async Task Category_cannot_be_its_own_parent_and_parent_with_children_cannot_be_hard_deleted()
    {
        var parent = new Category(TestDatabase.Unique("parent"), "Cha");
        var child = new Category(TestDatabase.Unique("child"), "Con", parent.Id);
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.AddRange(parent, child);
            await db.SaveChangesAsync();
        }

        var self = new Category(TestDatabase.Unique("self"), "Tự trỏ");
        self.ParentId = self.Id;
        await AssertViolates(self, PostgresErrorCodes.CheckViolation, "ck_categories_not_own_parent");

        await using AppDbContext db2 = TestDatabase.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db2.Database.ExecuteSqlRawAsync("DELETE FROM catalog.categories WHERE id = {0}", parent.Id));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    // ---------- Concurrency (xmin) ----------

    [Fact]
    public async Task Concurrent_price_edits_are_detected_with_xmin()
    {
        Product product = NewProduct(out ProductVariant variant);
        await Save(product);

        await using AppDbContext adminA = TestDatabase.CreateContext();
        await using AppDbContext adminB = TestDatabase.CreateContext();
        ProductVariant a = await adminA.Set<ProductVariant>().SingleAsync(v => v.Id == variant.Id);
        ProductVariant b = await adminB.Set<ProductVariant>().SingleAsync(v => v.Id == variant.Id);

        a.PriceVnd = 1_000_000;
        await adminA.SaveChangesAsync();

        b.PriceVnd = 900_000; // B sửa dựa trên dữ liệu cũ ⇒ phải bị chặn, không âm thầm ghi đè A
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => adminB.SaveChangesAsync());

        await using AppDbContext check = TestDatabase.CreateContext();
        Assert.Equal(1_000_000, (await check.Set<ProductVariant>().SingleAsync(v => v.Id == variant.Id)).PriceVnd);
    }

    // ---------- Helpers ----------

    private static Product NewProduct(out ProductVariant variant, string? slug = null, string? sku = null)
    {
        var product = new Product(SoftwareCategoryId, slug ?? TestDatabase.Unique("test-product"), "Microsoft 365 Family", LicenseType.Subscription)
        {
            Brand = "Microsoft",
            Platforms = [Platforms.Windows],
        };
        variant = new ProductVariant(product.Id, sku ?? TestDatabase.Unique("M365-FAM"), "12 tháng · 6 người dùng", 1_350_000, DeliveryType.LicenseKey);
        product.Variants.Add(variant);
        return product;
    }

    private static async Task Save(object entity)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    private static async Task AssertViolates(object entity, string sqlState, string constraint)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        db.Add(entity);
        DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        PostgresException pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(sqlState, pg.SqlState);
        Assert.Equal(constraint, pg.ConstraintName);
    }

    private static Task<T> Scalar<T>(AppDbContext db, string sql)
        => db.Database.SqlQueryRaw<T>($"SELECT ({sql}) AS \"Value\"").SingleAsync();
}
