using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Modules.Catalog.Features.Products;
using server.Modules.Inventory.Domain;
using server.Modules.Sales.Features.Cart;

namespace server.IntegrationTests.Phase3;

/// <summary>Dữ liệu riêng cho test báo giá: gói có 3 key, gói hết key, gói manual, gói ẩn, sản phẩm ẩn, gói có 25 key.</summary>
public sealed class CartData : IAsyncLifetime
{
    internal Product Product { get; private set; } = null!;
    internal ProductVariant ThreeKeys { get; private set; } = null!;
    internal ProductVariant NoKeys { get; private set; } = null!;
    internal ProductVariant Manual { get; private set; } = null!;
    internal ProductVariant InactiveVariant { get; private set; } = null!;
    internal ProductVariant InHiddenProduct { get; private set; } = null!;
    internal ProductVariant ManyKeys { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        await using AppDbContext db = TestDatabase.CreateContext();
        var category = new Category(TestDatabase.Unique("cart-cat"), "Giỏ hàng");
        Product = new Product(category.Id, TestDatabase.Unique("cart-prod"), "Microsoft 365 Test", LicenseType.Subscription);
        var hidden = new Product(category.Id, TestDatabase.Unique("cart-hidden"), "Ẩn", LicenseType.Perpetual) { IsActive = false };
        ThreeKeys = new ProductVariant(Product.Id, TestDatabase.Unique("K3"), "12 tháng", 1_290_000, DeliveryType.LicenseKey) { CompareAtPriceVnd = 1_590_000 };
        NoKeys = new ProductVariant(Product.Id, TestDatabase.Unique("K0"), "24 tháng", 2_390_000, DeliveryType.LicenseKey);
        Manual = new ProductVariant(Product.Id, TestDatabase.Unique("MAN"), "Cài đặt tận nơi", 300_000, DeliveryType.Manual);
        InactiveVariant = new ProductVariant(Product.Id, TestDatabase.Unique("OFF"), "Ngừng bán", 100_000, DeliveryType.Manual) { IsActive = false };
        InHiddenProduct = new ProductVariant(hidden.Id, TestDatabase.Unique("HID"), "Gói ẩn", 100_000, DeliveryType.Manual);
        ManyKeys = new ProductVariant(Product.Id, TestDatabase.Unique("K25"), "1 tháng", 99_000, DeliveryType.LicenseKey);
        db.AddRange(category, Product, hidden, ThreeKeys, NoKeys, Manual, InactiveVariant, InHiddenProduct, ManyKeys);
        await db.SaveChangesAsync();

        AddKeys(db, ThreeKeys, 3);
        AddKeys(db, ManyKeys, 25);
        await db.SaveChangesAsync();
    }

    private static void AddKeys(AppDbContext db, ProductVariant variant, int n)
    {
        var batch = new ImportBatch(variant.Id, ImportSource.Cli, "test.csv", DateTimeOffset.UtcNow);
        batch.Record(n, 0, 0);
        db.Add(batch);
        for (int i = 0; i < n; i++)
        {
            db.Add(new LicenseKey(variant.Id, batch.Id, RandomNumberGenerator.GetBytes(48), 1, RandomNumberGenerator.GetBytes(32), $"K{i:000}", DateTimeOffset.UtcNow));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class CartApiTests(ApiFactory factory, CartData data) : IClassFixture<ApiFactory>, IClassFixture<CartData>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<CartQuote> QuoteOk(params object[] items)
    {
        HttpResponseMessage res = await factory.CreateClient().PostAsJsonAsync("/api/cart/quote", new { items });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CartQuote>(Json))!;
    }

    [Fact]
    public async Task Prices_and_totals_come_from_the_database_not_the_client()
    {
        // Client cố gửi giá 1đ — bị bỏ qua.
        CartQuote q = await QuoteOk(
            new { variantId = data.ThreeKeys.Id, quantity = 2, priceVnd = 1 },
            new { variantId = data.Manual.Id, quantity = 1 });

        Assert.True(q.CanCheckout);
        Assert.Equal(2 * 1_290_000 + 300_000, q.TotalVnd);
        Assert.Equal(q.SubtotalVnd, q.TotalVnd);
        Assert.Equal(0, q.DiscountVnd);
        CartQuoteLine line = q.Lines[0];
        Assert.Equal(CartLineStatus.Ok, line.Status);
        Assert.Equal(1_290_000, line.UnitPriceVnd);
        Assert.Equal(1_590_000, line.CompareAtPriceVnd);
        Assert.Equal(data.Product.Slug, line.ProductSlug);
        Assert.Equal("license_key", line.DeliveryType);
        Assert.Null(line.AvailableQuantity);   // đủ hàng ⇒ không trả số tồn
    }

    [Fact]
    public async Task Short_stock_reports_available_quantity_and_blocks_checkout()
    {
        CartQuote q = await QuoteOk(new { variantId = data.ThreeKeys.Id, quantity = 5 }, new { variantId = data.Manual.Id, quantity = 1 });

        Assert.False(q.CanCheckout);
        Assert.Equal(CartLineStatus.InsufficientStock, q.Lines[0].Status);
        Assert.Equal(3, q.Lines[0].AvailableQuantity);
        Assert.Equal(300_000, q.TotalVnd);   // dòng thiếu hàng không tính vào tổng
    }

    [Fact]
    public async Task No_keys_means_out_of_stock_but_manual_items_need_no_stock()
    {
        CartQuote q = await QuoteOk(new { variantId = data.NoKeys.Id, quantity = 1 }, new { variantId = data.Manual.Id, quantity = 20 });
        Assert.Equal(CartLineStatus.OutOfStock, q.Lines[0].Status);
        Assert.Equal(0, q.Lines[0].AvailableQuantity);
        Assert.Equal(CartLineStatus.Ok, q.Lines[1].Status);
    }

    [Fact]
    public async Task Stock_level_is_never_exposed_beyond_the_requested_need()
    {
        CartQuote q = await QuoteOk(new { variantId = data.ManyKeys.Id, quantity = 5 });
        Assert.Equal(CartLineStatus.Ok, q.Lines[0].Status);
        Assert.Null(q.Lines[0].AvailableQuantity);   // còn 25 nhưng không lộ con số
    }

    [Fact]
    public async Task Hidden_inactive_and_unknown_variants_are_unavailable_without_failing_the_request()
    {
        var unknown = Guid.NewGuid();
        CartQuote q = await QuoteOk(
            new { variantId = data.InactiveVariant.Id, quantity = 1 },
            new { variantId = data.InHiddenProduct.Id, quantity = 1 },
            new { variantId = unknown, quantity = 1 },
            new { variantId = data.Manual.Id, quantity = 1 });

        Assert.Equal(
            [CartLineStatus.Unavailable, CartLineStatus.Unavailable, CartLineStatus.Unavailable, CartLineStatus.Ok],
            q.Lines.Select(l => l.Status));
        Assert.Null(q.Lines[1].ProductName);   // không lộ thông tin sản phẩm ẩn
        Assert.False(q.CanCheckout);
        Assert.Equal(300_000, q.TotalVnd);
    }

    [Fact]
    public async Task Duplicate_lines_are_merged()
    {
        CartQuote q = await QuoteOk(new { variantId = data.Manual.Id, quantity = 2 }, new { variantId = data.Manual.Id, quantity = 3 });
        CartQuoteLine line = Assert.Single(q.Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(1_500_000, q.TotalVnd);
    }

    [Theory]
    [InlineData("""{"items":[]}""", "items")]
    [InlineData("""{}""", "items")]
    [InlineData("""{"items":[{"variantId":"00000000-0000-0000-0000-000000000001","quantity":0}]}""", "items[0].quantity")]
    [InlineData("""{"items":[{"variantId":"00000000-0000-0000-0000-000000000001","quantity":21}]}""", "items[0].quantity")]
    [InlineData("""{"items":[{"variantId":"00000000-0000-0000-0000-000000000000","quantity":1}]}""", "items[0].variantId")]
    [InlineData("""{"items":[{"variantId":"00000000-0000-0000-0000-000000000001","quantity":15},{"variantId":"00000000-0000-0000-0000-000000000001","quantity":6}]}""", "items[0].quantity")]
    public async Task Invalid_carts_get_400_with_field_errors(string body, string field)
    {
        HttpResponseMessage res = await factory.CreateClient().PostAsync("/api/cart/quote",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task More_than_20_lines_is_rejected()
    {
        object[] items = Enumerable.Range(0, 21).Select(_ => (object)new { variantId = Guid.NewGuid(), quantity = 1 }).ToArray();
        HttpResponseMessage res = await factory.CreateClient().PostAsJsonAsync("/api/cart/quote", new { items });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Product_detail_reports_in_stock_per_variant()
    {
        ProductDetail? d = await factory.CreateClient().GetFromJsonAsync<ProductDetail>($"/api/products/{data.Product.Slug}", Json);
        Assert.NotNull(d);
        Assert.True(d!.Variants.Single(v => v.Id == data.ThreeKeys.Id).InStock);
        Assert.False(d.Variants.Single(v => v.Id == data.NoKeys.Id).InStock);
        Assert.True(d.Variants.Single(v => v.Id == data.Manual.Id).InStock);
    }
}

public class CartRateLimitTests
{
    private sealed class TightLimits : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:RateLimits:CartQuotePerMinute", "2");
        }
    }

    [Fact]
    public async Task Third_quote_in_a_minute_gets_429()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new TightLimits();
        HttpClient client = factory.CreateClient();
        var body = new { items = new[] { new { variantId = Guid.NewGuid(), quantity = 1 } } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/cart/quote", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/cart/quote", body)).StatusCode);
        HttpResponseMessage limited = await client.PostAsJsonAsync("/api/cart/quote", body);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }
}
