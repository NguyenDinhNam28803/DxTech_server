using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Modules.Catalog.Features.Categories;
using server.Modules.Catalog.Features.Products;
using server.Shared.Paging;

namespace server.IntegrationTests.Catalog;

/// <summary>
/// Dữ liệu riêng cho test API Catalog: một cây danh mục và các sản phẩm mang "từ đánh dấu" ngẫu nhiên,
/// để test không đụng dữ liệu của test khác trên cùng database.
/// </summary>
public sealed class CatalogApiData : IAsyncLifetime
{
    public string Marker { get; } = TestDatabase.Word();      // có trong tên mọi sản phẩm của bộ dữ liệu
    public string RankWord { get; } = TestDatabase.Word();    // có trong tên P1, trong mô tả ngắn của P3

    internal Category Root { get; private set; } = null!;
    internal Category Child { get; private set; } = null!;
    internal Category HiddenChild { get; private set; } = null!;
    internal Product Design { get; private set; } = null!;      // P1 — trong Child, 2 gói đang bán + 1 gói ẩn
    internal Product Windows { get; private set; } = null!;     // P2 — trong Root, rẻ nhất
    internal Product Security { get; private set; } = null!;    // P3 — trong Child, mới nhất
    internal Product Hidden { get; private set; } = null!;
    internal Product NoVariant { get; private set; } = null!;
    internal Product Deleted { get; private set; } = null!;
    internal Product InHiddenCategory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        Root = new Category(TestDatabase.Unique("t-root"), "Gốc test") { SortOrder = 1 };
        Child = new Category(TestDatabase.Unique("t-child"), "Con test", Root.Id) { SortOrder = 1 };
        HiddenChild = new Category(TestDatabase.Unique("t-hidden"), "Con ẩn", Root.Id) { SortOrder = 2, IsActive = false };
        await Save(t0, Root, Child, HiddenChild);

        Design = NewProduct(Child, $"Phần mềm Đồ hoạ {Marker} {RankWord}", LicenseType.Subscription, [Platforms.Windows, Platforms.MacOs]);
        Design.Brand = "Adobe";
        Design.Features = ["Bộ công cụ thiết kế", "Cloud 100GB"];
        AddVariant(Design, 2_000_000, 2_500_000, sortOrder: 1, isDefault: true);
        AddVariant(Design, 1_500_000, 1_900_000, sortOrder: 2);
        AddVariant(Design, 100, null, sortOrder: 3).IsActive = false;
        await Save(t0.AddMinutes(1), Design);

        Windows = NewProduct(Root, $"Windows Pro {Marker}", LicenseType.Perpetual, [Platforms.Windows]);
        AddVariant(Windows, 690_000, 1_490_000);
        await Save(t0.AddMinutes(2), Windows);

        Security = NewProduct(Child, $"Bảo mật Kaspersky {Marker}", LicenseType.Subscription, [Platforms.Windows, Platforms.Android]);
        Security.ShortDescription = $"Diệt virus {RankWord}";
        AddVariant(Security, 3_000_000, null);
        await Save(t0.AddMinutes(3), Security);

        Hidden = NewProduct(Root, $"Ẩn {Marker}", LicenseType.Perpetual, []);
        Hidden.IsActive = false;
        AddVariant(Hidden, 1_000, null);

        NoVariant = NewProduct(Root, $"Hết gói {Marker}", LicenseType.Perpetual, []);
        AddVariant(NoVariant, 1_000, null).IsActive = false;

        Deleted = NewProduct(Root, $"Đã xoá {Marker}", LicenseType.Perpetual, []);
        Deleted.DeletedAt = t0;
        AddVariant(Deleted, 1_000, null);

        InHiddenCategory = NewProduct(HiddenChild, $"Trong danh mục ẩn {Marker}", LicenseType.Perpetual, []);
        AddVariant(InHiddenCategory, 1_000, null);
        await Save(t0.AddMinutes(4), Hidden, NoVariant, Deleted, InHiddenCategory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Product NewProduct(Category category, string name, LicenseType license, List<string> platforms)
        => new(category.Id, TestDatabase.Unique("p"), name, license) { Platforms = platforms };

    private static ProductVariant AddVariant(Product product, long price, long? compareAt, int sortOrder = 0, bool isDefault = false)
    {
        var variant = new ProductVariant(product.Id, TestDatabase.Unique("SKU"), $"Gói {price}", price, DeliveryType.LicenseKey)
        {
            CompareAtPriceVnd = compareAt,
            SortOrder = sortOrder,
            IsDefault = isDefault,
        };
        product.Variants.Add(variant);
        return variant;
    }

    private static async Task Save(DateTimeOffset at, params object[] entities)
    {
        await using AppDbContext db = TestDatabase.CreateContext(new FixedTime(at));
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>PR-4: API Catalog qua HTTP thật (routing, binding, JSON, ProblemDetails) trên PostgreSQL thật.</summary>
public class CatalogApiTests(ApiFactory factory, CatalogApiData data) : IClassFixture<ApiFactory>, IClassFixture<CatalogApiData>
{
    private readonly HttpClient _client = factory.CreateClient();

    // ---------- GET /api/categories ----------

    [Fact]
    public async Task Category_tree_nests_children_and_hides_inactive_branches()
    {
        List<CategoryNode> tree = (await _client.GetFromJsonAsync<List<CategoryNode>>("/api/categories"))!;

        CategoryNode root = Assert.Single(tree, n => n.Slug == data.Root.Slug);
        CategoryNode child = Assert.Single(root.Children);
        Assert.Equal(data.Child.Slug, child.Slug);
        Assert.Empty(child.Children);
        Assert.Contains(tree, n => n.Slug == "software"); // danh mục gốc từ migration
    }

    [Fact]
    public async Task Json_carries_vietnamese_as_utf8_not_unicode_escapes()
    {
        string raw = await _client.GetStringAsync($"/api/products/{data.Design.Slug}");

        Assert.Contains("Phần mềm Đồ hoạ", raw);
        Assert.DoesNotContain("\\u", raw);
    }

    // ---------- GET /api/products: lọc ----------

    [Fact]
    public async Task Category_filter_includes_descendants_and_only_listed_products()
    {
        PagedResult<ProductListItem> page = await List($"category={data.Root.Slug}");

        Assert.Equal(3, page.Total);
        Assert.Equal(
            new[] { data.Design.Slug, data.Windows.Slug, data.Security.Slug }.Order(),
            page.Items.Select(i => i.Slug).Order());
    }

    [Fact]
    public async Task Child_category_filter_only_returns_its_products()
    {
        PagedResult<ProductListItem> page = await List($"category={data.Child.Slug}");

        Assert.Equal(new[] { data.Design.Slug, data.Security.Slug }.Order(), page.Items.Select(i => i.Slug).Order());
    }

    [Theory]
    [InlineData("platform=android", "Security")]
    [InlineData("platform=macos", "Design")]
    [InlineData("licenseType=perpetual", "Windows")]
    [InlineData("minPrice=1000000&maxPrice=1600000", "Design")]   // nhờ gói 1.500.000
    [InlineData("maxPrice=700000", "Windows")]
    public async Task Filters_narrow_the_list(string filter, string expected)
    {
        PagedResult<ProductListItem> page = await List($"category={data.Root.Slug}&{filter}");

        ProductListItem item = Assert.Single(page.Items);
        Assert.Equal(Slug(expected), item.Slug);
    }

    [Fact]
    public async Task Inactive_variant_prices_are_ignored()
    {
        PagedResult<ProductListItem> page = await List($"category={data.Root.Slug}&maxPrice=200");

        Assert.Equal(0, page.Total); // gói 100đ của "Đồ hoạ" đang ẩn
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Card_shows_cheapest_active_variant_with_its_own_compare_at_price()
    {
        PagedResult<ProductListItem> page = await List($"category={data.Child.Slug}&platform=macos");

        ProductListItem design = Assert.Single(page.Items);
        Assert.Equal(1_500_000, design.MinPriceVnd);
        Assert.Equal(1_900_000, design.CompareAtPriceVnd);
        Assert.Equal("Gói 1500000", design.VariantName); // tên của chính gói rẻ nhất, không phải gói mặc định
        Assert.Equal(2, design.VariantCount);
        Assert.Equal("subscription", design.LicenseType);
        Assert.Equal(data.Child.Slug, design.Category.Slug);
        Assert.Equal(["windows", "macos"], design.Platforms);
    }

    // ---------- Sắp xếp & phân trang ----------

    [Theory]
    [InlineData("price_asc", "Windows,Design,Security")]
    [InlineData("price_desc", "Security,Design,Windows")]
    [InlineData("newest", "Security,Windows,Design")]
    public async Task Sorting(string sort, string expectedOrder)
    {
        PagedResult<ProductListItem> page = await List($"category={data.Root.Slug}&sort={sort}");

        Assert.Equal(expectedOrder.Split(',').Select(Slug), page.Items.Select(i => i.Slug));
    }

    [Fact]
    public async Task Default_sort_without_query_is_newest()
    {
        PagedResult<ProductListItem> page = await List($"category={data.Root.Slug}");

        Assert.Equal(data.Security.Slug, page.Items[0].Slug);
    }

    [Fact]
    public async Task Paging_is_stable_and_reports_totals()
    {
        PagedResult<ProductListItem> first = await List($"category={data.Root.Slug}&sort=price_asc&pageSize=2&page=1");
        PagedResult<ProductListItem> second = await List($"category={data.Root.Slug}&sort=price_asc&pageSize=2&page=2");
        PagedResult<ProductListItem> beyond = await List($"category={data.Root.Slug}&sort=price_asc&pageSize=2&page=5");

        Assert.Equal(3, first.Total);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(data.Security.Slug, Assert.Single(second.Items).Slug);
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task Page_size_defaults_to_12_and_is_capped_at_100()
    {
        Assert.Equal(12, (await List($"category={data.Root.Slug}")).PageSize);
        Assert.Equal(100, (await List($"category={data.Root.Slug}&pageSize=5000")).PageSize);
        Assert.Equal(1, (await List($"category={data.Root.Slug}&page=-3")).Page);
    }

    // ---------- Lỗi ----------

    [Fact]
    public async Task Unknown_or_hidden_category_is_404_with_code()
    {
        await AssertProblem($"/api/products?category=khong-ton-tai-{data.Marker}", HttpStatusCode.NotFound, "catalog.category_not_found");
        await AssertProblem($"/api/products?category={data.HiddenChild.Slug}", HttpStatusCode.NotFound, "catalog.category_not_found");
    }

    [Theory]
    [InlineData("minPrice=500&maxPrice=100", "maxPrice")]
    [InlineData("minPrice=-1", "minPrice")]
    [InlineData("platform=Windows", "platform")]
    [InlineData("licenseType=lifetime", "licenseType")]
    [InlineData("sort=cheapest", "sort")]
    [InlineData("sort=relevance", "sort")]
    public async Task Invalid_parameters_are_400_with_field_errors(string query, string field)
    {
        JsonElement problem = await AssertProblem($"/api/products?{query}", HttpStatusCode.BadRequest, "catalog.invalid_query");

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"thiếu lỗi cho '{field}'");
    }

    [Fact]
    public async Task Malformed_number_is_rejected_by_model_binding()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/products?minPrice=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---------- GET /api/products/{slug} ----------

    [Fact]
    public async Task Detail_returns_active_variants_breadcrumb_and_features()
    {
        ProductDetail detail = (await _client.GetFromJsonAsync<ProductDetail>($"/api/products/{data.Design.Slug}"))!;

        Assert.Equal(data.Design.Id, detail.Id);
        Assert.Equal("Adobe", detail.Brand);
        Assert.Equal(["Bộ công cụ thiết kế", "Cloud 100GB"], detail.Features);
        Assert.Equal(new[] { data.Root.Slug, data.Child.Slug }, detail.Breadcrumb.Select(b => b.Slug));
        Assert.Equal(new long[] { 2_000_000, 1_500_000 }, detail.Variants.Select(v => v.PriceVnd)); // theo sort_order, bỏ gói ẩn
        Assert.True(detail.Variants[0].IsDefault);
        Assert.Equal("license_key", detail.Variants[0].DeliveryType);
    }

    [Theory]
    [InlineData("Hidden")]
    [InlineData("NoVariant")]
    [InlineData("Deleted")]
    [InlineData("InHiddenCategory")]
    [InlineData("missing")]
    public async Task Detail_of_unpublished_product_is_404(string which)
    {
        string slug = which == "missing" ? $"khong-co-{data.Marker}" : Slug(which);

        await AssertProblem($"/api/products/{slug}", HttpStatusCode.NotFound, "catalog.product_not_found");
    }

    // ---------- GET /api/products/search ----------

    [Theory]
    [InlineData("phan mem do hoa")]   // không dấu
    [InlineData("PHẦN MỀM ĐỒ HOẠ")]   // có dấu, chữ hoa
    [InlineData("phan me")]           // đang gõ dở: khớp tiền tố
    [InlineData("adobe")]             // theo hãng
    public async Task Search_ignores_vietnamese_marks_and_case(string words)
    {
        PagedResult<ProductListItem> page = await Search($"{words} {data.Marker}");

        Assert.Equal(data.Design.Slug, Assert.Single(page.Items).Slug);
    }

    [Fact]
    public async Task Search_ranks_name_matches_above_description_matches()
    {
        PagedResult<ProductListItem> page = await Search(data.RankWord);

        Assert.Equal(new[] { data.Design.Slug, data.Security.Slug }, page.Items.Select(i => i.Slug));
    }

    [Fact]
    public async Task Search_combines_with_filters_and_hides_unpublished_products()
    {
        PagedResult<ProductListItem> all = await Search(data.Marker);
        PagedResult<ProductListItem> inChild = await Search(data.Marker, $"&category={data.Child.Slug}");

        Assert.Equal(3, all.Total); // Ẩn / Hết gói / Đã xoá / Trong danh mục ẩn không xuất hiện
        Assert.Equal(new[] { data.Design.Slug, data.Security.Slug }.Order(), inChild.Items.Select(i => i.Slug).Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("%26%7C!%3A*()")]   // chỉ toàn ký tự đặc biệt của tsquery
    public async Task Search_without_words_is_400(string q)
    {
        await AssertProblem($"/api/products/search?q={q}", HttpStatusCode.BadRequest, "catalog.search_query_required");
    }

    [Theory]
    [InlineData("a' | b) & !c:*")]
    [InlineData("'; DROP TABLE catalog.products; --")]
    [InlineData("\\\\ \" \u0000")]
    public async Task Hostile_search_input_never_causes_a_server_error(string q)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/products/search?q={Uri.EscapeDataString(q)}");

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"nhận {(int)response.StatusCode}");
    }

    // ---------- Helpers ----------

    private string Slug(string which) => which switch
    {
        "Design" => data.Design.Slug,
        "Windows" => data.Windows.Slug,
        "Security" => data.Security.Slug,
        "Hidden" => data.Hidden.Slug,
        "NoVariant" => data.NoVariant.Slug,
        "Deleted" => data.Deleted.Slug,
        "InHiddenCategory" => data.InHiddenCategory.Slug,
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private async Task<PagedResult<ProductListItem>> List(string query)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/products?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<ProductListItem>>())!;
    }

    private async Task<PagedResult<ProductListItem>> Search(string q, string extra = "")
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/products/search?q={Uri.EscapeDataString(q)}{extra}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<ProductListItem>>())!;
    }

    private async Task<JsonElement> AssertProblem(string url, HttpStatusCode status, string code)
    {
        HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }
}
