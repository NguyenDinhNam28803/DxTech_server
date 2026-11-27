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
using server.Modules.Catalog.Domain;
using server.Modules.Inventory.Domain;
using server.Modules.Inventory.Features.Import;
using server.Modules.Sales.Domain;
using server.Modules.Sales.Features.Orders;
using server.Modules.Sales.Jobs;

namespace server.IntegrationTests.Phase3;

/// <summary>
/// Đặt / xem / huỷ đơn, job hết hạn, nhập key (PR-11) trên PostgreSQL thật qua HTTP như web gọi.
/// Mỗi test tạo gói + key riêng ⇒ chạy độc lập, chạy lại được.
/// </summary>
public class OrdersApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Password = "Ngựa vằn chạy 42 vòng";

    // ---------- Hỗ trợ ----------

    private async Task<string> NewCustomerTokenAsync()
    {
        HttpResponseMessage res = await factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { email = $"{TestDatabase.Unique("buyer")}@dxtechs.test", password = Password, fullName = "Khách Đặt Hàng" });
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Gói mới; nếu là license_key thì nhập <paramref name="keys"/> key qua đúng đường của lệnh --import-keys.</summary>
    private async Task<ProductVariant> NewVariantAsync(int keys, DeliveryType delivery = DeliveryType.LicenseKey, long price = 1_290_000)
    {
        ProductVariant variant;
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            var category = new Category(TestDatabase.Unique("ord-cat"), "Đơn hàng");
            var product = new Product(category.Id, TestDatabase.Unique("ord-prod"), "Phần mềm đặt hàng", LicenseType.Subscription);
            variant = new ProductVariant(product.Id, TestDatabase.Unique("ORD"), "12 tháng", price, delivery);
            db.AddRange(category, product, variant);
            await db.SaveChangesAsync();
        }

        if (keys > 0)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            ImportReport r = await scope.ServiceProvider.GetRequiredService<KeyImporter>().RunAsync(
                variant.Sku, Enumerable.Range(0, keys).Select(_ => $"KEY-{Guid.NewGuid():N}"), "test.csv", null, false, default);
            Assert.Equal(keys, r.Inserted);
        }

        return variant;
    }

    private async Task<HttpResponseMessage> PlaceAsync(string token, Guid variantId, int quantity, Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { items = new[] { new { variantId, quantity } } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", (idempotencyKey ?? Guid.NewGuid()).ToString());
        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    private static async Task<OrderView> ReadOrder(HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<OrderView>(Json))!;

    private static async Task<string?> ProblemCode(HttpResponseMessage res)
    {
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;
    }

    private static async Task<Dictionary<string, int>> KeyStatusesAsync(Guid variantId)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        return await db.Set<LicenseKey>().Where(k => k.VariantId == variantId)
            .GroupBy(k => k.Status).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key.ToString(), x => x.N);
    }

    // ---------- Đặt hàng ----------

    [Fact]
    public async Task Placing_an_order_reserves_keys_and_returns_vietqr_instructions()
    {
        ProductVariant variant = await NewVariantAsync(keys: 3);
        string token = await NewCustomerTokenAsync();

        HttpResponseMessage res = await PlaceAsync(token, variant.Id, 2);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        OrderView order = await ReadOrder(res);
        Assert.Equal($"/api/orders/{order.Code}", res.Headers.Location?.AbsolutePath);
        Assert.True(OrderCode.IsValid(order.Code));
        Assert.Equal("pending_payment", order.Status);
        Assert.Equal(2_580_000, order.TotalVnd);
        // Hạn 30 phút tính từ lúc handler tạo đơn; created_at do interceptor ghi khi lưu (chênh vài mili-giây).
        Assert.InRange(order.ExpiresAt - order.CreatedAt, TimeSpan.FromMinutes(29.9), TimeSpan.FromMinutes(30));
        OrderLineView line = Assert.Single(order.Lines);
        Assert.Empty(line.Keys);                                    // chưa trả tiền ⇒ không có key
        Assert.NotNull(order.Payment);
        Assert.Equal(order.Code, order.Payment!.TransferContent);
        Assert.Equal(2_580_000, order.Payment.AmountVnd);
        Assert.Contains("0810" + order.Code, order.Payment.VietQr);
        Assert.Equal("pending_payment", Assert.Single(order.Timeline).ToStatus);

        Dictionary<string, int> keys = await KeyStatusesAsync(variant.Id);
        Assert.Equal(2, keys["Reserved"]);
        Assert.Equal(1, keys["Available"]);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_order()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        string token = await NewCustomerTokenAsync();
        var key = Guid.NewGuid();

        HttpResponseMessage first = await PlaceAsync(token, variant.Id, 1, key);
        HttpResponseMessage again = await PlaceAsync(token, variant.Id, 1, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await ReadOrder(first)).Code, (await ReadOrder(again)).Code);
    }

    [Fact]
    public async Task More_than_in_stock_is_rejected_and_nothing_stays_reserved()
    {
        ProductVariant variant = await NewVariantAsync(keys: 3);
        HttpResponseMessage res = await PlaceAsync(await NewCustomerTokenAsync(), variant.Id, 4);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("sales.cart_not_available", await ProblemCode(res));
        Assert.Equal(3, (await KeyStatusesAsync(variant.Id))["Available"]);
    }

    [Fact]
    public async Task Concurrent_buyers_never_get_the_same_key()
    {
        ProductVariant variant = await NewVariantAsync(keys: 10);
        string[] tokens = new string[25];
        for (int i = 0; i < tokens.Length; i++)
        {
            tokens[i] = await NewCustomerTokenAsync();
        }

        HttpResponseMessage[] results = await Task.WhenAll(tokens.Select(t => PlaceAsync(t, variant.Id, 1)));

        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(15, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await using AppDbContext db = TestDatabase.CreateContext();
        List<LicenseKey> keys = await db.Set<LicenseKey>().Where(k => k.VariantId == variant.Id).ToListAsync();
        Assert.All(keys, k => Assert.Equal(LicenseKeyStatus.Reserved, k.Status));
        Assert.Equal(10, keys.Select(k => k.OrderItemId).Distinct().Count());   // mỗi key một đơn khác nhau
    }

    [Fact]
    public async Task At_most_three_pending_orders_per_account()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        string token = await NewCustomerTokenAsync();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(token, variant.Id, 1)).StatusCode);
        }

        HttpResponseMessage fourth = await PlaceAsync(token, variant.Id, 1);
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("sales.too_many_pending", await ProblemCode(fourth));
    }

    [Fact]
    public async Task Ordering_requires_sign_in_and_an_idempotency_key()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        HttpResponseMessage anonymous = await factory.CreateClient().PostAsJsonAsync("/api/orders", new { items = new[] { new { variantId = variant.Id, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { items = new[] { new { variantId = variant.Id, quantity = 1 } } }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await NewCustomerTokenAsync());
        HttpResponseMessage noKey = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    }

    [Fact]
    public async Task Invalid_vat_info_gets_field_errors()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { items = new[] { new { variantId = variant.Id, quantity = 1 } }, vatInfo = new { companyName = "DX", taxCode = "123", address = "HN" } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await NewCustomerTokenAsync());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        HttpResponseMessage res = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("vatInfo.taxCode", await res.Content.ReadAsStringAsync());
    }

    // ---------- Xem, danh sách, huỷ ----------

    [Fact]
    public async Task Owner_can_view_and_cancel_others_get_404()
    {
        ProductVariant variant = await NewVariantAsync(keys: 2);
        string owner = await NewCustomerTokenAsync();
        string stranger = await NewCustomerTokenAsync();
        string code = (await ReadOrder(await PlaceAsync(owner, variant.Id, 2))).Code;

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/orders/{code.ToLowerInvariant()}", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/orders/{code}", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/orders/{code}/cancel", stranger)).StatusCode);

        HttpResponseMessage cancelled = await SendAsync(HttpMethod.Post, $"/api/orders/{code}/cancel", owner);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        OrderView view = await ReadOrder(cancelled);
        Assert.Equal("cancelled", view.Status);
        Assert.Equal("customer", view.CancelReason);
        Assert.Null(view.Payment);
        Assert.Equal(["pending_payment", "cancelled"], view.Timeline.Select(t => t.ToStatus));
        Assert.Equal(2, (await KeyStatusesAsync(variant.Id))["Available"]);   // key về kho ngay

        HttpResponseMessage again = await SendAsync(HttpMethod.Post, $"/api/orders/{code}/cancel", owner);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task My_orders_lists_newest_first_with_cursor_paging()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        string token = await NewCustomerTokenAsync();
        string first = (await ReadOrder(await PlaceAsync(token, variant.Id, 1))).Code;
        string second = (await ReadOrder(await PlaceAsync(token, variant.Id, 2))).Code;

        HttpResponseMessage res = await SendAsync(HttpMethod.Get, "/api/me/orders", token);
        OrderPage page = (await res.Content.ReadFromJsonAsync<OrderPage>(Json))!;
        Assert.Equal([second, first], page.Items.Select(o => o.Code));
        Assert.Null(page.NextCursor);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/api/me/orders?cursor=%%%", token)).StatusCode);
    }

    [Fact]
    public async Task Keys_are_shown_only_after_delivery()
    {
        ProductVariant variant = await NewVariantAsync(keys: 2);
        string token = await NewCustomerTokenAsync();
        OrderView placed = await ReadOrder(await PlaceAsync(token, variant.Id, 2));

        // Giả lập PR-12: tiền về ⇒ giao key, đơn hoàn tất.
        await using (var conn = new NpgsqlConnection(ApiFactory.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"""
                UPDATE inventory.license_keys SET status = 'delivered', delivered_at = now() WHERE variant_id = '{variant.Id}' AND status = 'reserved';
                UPDATE sales.orders SET status = 'paid', paid_at = now() WHERE code = '{placed.Code}';
                UPDATE sales.orders SET status = 'fulfilled', fulfilled_at = now() WHERE code = '{placed.Code}';
                """, conn).ExecuteNonQueryAsync();
        }

        OrderView view = await ReadOrder(await SendAsync(HttpMethod.Get, $"/api/orders/{placed.Code}", token));
        Assert.Equal("fulfilled", view.Status);
        Assert.Equal(2, view.Lines[0].Keys.Count);
        Assert.All(view.Lines[0].Keys, k => Assert.StartsWith("KEY-", k));
        Assert.Null(view.Payment);
    }

    // ---------- Job hết hạn ----------

    [Fact]
    public async Task Expired_orders_are_cancelled_and_keys_return_to_stock()
    {
        ProductVariant variant = await NewVariantAsync(keys: 2);
        string token = await NewCustomerTokenAsync();
        string code = (await ReadOrder(await PlaceAsync(token, variant.Id, 2))).Code;
        await using (var conn = new NpgsqlConnection(ApiFactory.ConnectionString))
        {
            await conn.OpenAsync();
            // Dời đơn về 31 phút trước (giữ ck_orders_expires: expires_at > created_at).
            await new NpgsqlCommand($"UPDATE sales.orders SET created_at = created_at - interval '31 minutes', expires_at = created_at - interval '1 minute' WHERE code = '{code}'", conn)
                .ExecuteNonQueryAsync();
        }

        int cancelled = await factory.Services.GetRequiredService<OrderExpiry>().RunOnceAsync(default);

        Assert.True(cancelled >= 1);
        OrderView view = await ReadOrder(await SendAsync(HttpMethod.Get, $"/api/orders/{code}", token));
        Assert.Equal("cancelled", view.Status);
        Assert.Equal("expired", view.CancelReason);
        OrderEventView last = view.Timeline[^1];
        Assert.Equal(("system", "expired"), (last.ActorType, last.Reason));
        Assert.Equal(2, (await KeyStatusesAsync(variant.Id))["Available"]);
    }

    // ---------- Nhập key ----------

    [Fact]
    public async Task Import_skips_duplicates_and_invalid_lines_and_stores_keys_encrypted()
    {
        ProductVariant variant = await NewVariantAsync(keys: 0);
        string key = $"ABCDE-{Guid.NewGuid():N}";
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        KeyImporter importer = scope.ServiceProvider.GetRequiredService<KeyImporter>();

        ImportReport dry = await importer.RunAsync(variant.Sku, [key], "keys.csv", null, dryRun: true, default);
        Assert.True(dry.DryRun);
        Assert.Empty(await KeyStatusesAsync(variant.Id));   // dry-run không ghi gì

        ImportReport r = await importer.RunAsync(variant.Sku,
            ["# dòng chú thích", key, key.ToLowerInvariant().Replace("-", " "), "", "abc", $"XYZ-{Guid.NewGuid():N}"],
            "keys.csv", "HĐ 0001", dryRun: false, default);
        Assert.Equal((4, 2, 1), (r.Total, r.Inserted, r.Duplicates));
        Assert.Equal([5], r.Invalid);                         // dòng 5 ("abc") quá ngắn

        ImportReport again = await importer.RunAsync(variant.Sku, [key], "keys.csv", null, false, default);
        Assert.Equal((0, 1), (again.Inserted, again.Duplicates));   // đã có trong kho

        await using AppDbContext db = TestDatabase.CreateContext();
        string hint = key[^4..].ToUpperInvariant();
        LicenseKey stored = await db.Set<LicenseKey>().FirstAsync(k => k.VariantId == variant.Id && k.KeyHint == hint);
        Assert.False(Encoding.UTF8.GetString(stored.KeyEncrypted).Contains(key[6..20], StringComparison.OrdinalIgnoreCase));
        ImportBatch batch = await db.Set<ImportBatch>().SingleAsync(b => b.VariantId == variant.Id && b.Note == "HĐ 0001");
        Assert.Equal((4, 2, 1, 1), (batch.TotalRows, batch.InsertedRows, batch.DuplicateRows, batch.InvalidRows));
    }

    [Fact]
    public async Task Import_refuses_variants_that_are_not_delivered_by_license_key()
    {
        ProductVariant manual = await NewVariantAsync(keys: 0, DeliveryType.Manual);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<KeyImporter>().RunAsync(manual.Sku, ["KEY-12345"], "x", null, false, default));
    }
}

public class OrderingConfigurationTests
{
    private sealed class NoBankAccount : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Payments:BankTransfer:BankBin", "");
            builder.UseSetting("Payments:BankTransfer:AccountNumber", "");
        }
    }

    private sealed class NoLicenseKeys : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("License:EncryptionKeys:1", "");
        }
    }

    private sealed class TightOrders : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:RateLimits:OrdersPerHour", "2");
        }
    }

    [Fact]
    public async Task Without_a_bank_account_ordering_is_unavailable_but_the_api_runs()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new NoBankAccount();
        HttpClient client = factory.CreateClient();
        string token = await RegisterAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { items = new[] { new { variantId = Guid.NewGuid(), quantity = 1 } } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        HttpResponseMessage res = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("sales.ordering_unavailable", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public void Missing_license_encryption_key_stops_startup()
    {
        using var factory = new NoLicenseKeys();
        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("LICENSE_ENC_KEY_V1", ex.ToString());
    }

    [Fact]
    public async Task Third_order_request_in_an_hour_from_one_ip_gets_429()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new TightOrders();
        HttpClient client = factory.CreateClient();
        var body = new { items = new[] { new { variantId = Guid.NewGuid(), quantity = 1 } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/orders", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/orders", body)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.PostAsJsonAsync("/api/orders", body)).StatusCode);
    }

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        HttpResponseMessage res = await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"{TestDatabase.Unique("cfg")}@dxtechs.test", password = "Ngựa vằn chạy 42 vòng", fullName = "Cấu Hình" });
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }
}
