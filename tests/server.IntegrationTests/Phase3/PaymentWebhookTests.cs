using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Modules.Inventory.Features.Import;
using server.Modules.Payments.Domain;
using server.Modules.Payments.Features.Sepay;
using server.Modules.Payments.Jobs;
using server.Modules.Sales.Features.Orders;
using server.Modules.Sales.Jobs;

namespace server.IntegrationTests.Phase3;

/// <summary>Webhook SePay → áp tiền → giao key (PR-12, ADR-013 §3–4) trên PostgreSQL thật, qua HTTP như SePay gọi.</summary>
public class PaymentWebhookTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Password = "Ngựa vằn chạy 42 vòng";

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Hỗ trợ ----------

    private async Task<string> TokenAsync(bool admin = false)
    {
        string email = $"{TestDatabase.Unique(admin ? "padmin" : "payer")}@dxtechs.test";
        HttpClient c = factory.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/register", new { email, password = Password, fullName = "Người Trả Tiền" })).EnsureSuccessStatusCode();
        if (admin)
        {
            await SqlAsync($"UPDATE identity.users SET role = 'admin' WHERE email = '{email}'");
        }

        HttpResponseMessage login = await c.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<ProductVariant> VariantAsync(int keys, DeliveryType delivery = DeliveryType.LicenseKey, long price = 1_000_000)
    {
        ProductVariant v;
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            var cat = new Category(TestDatabase.Unique("pay-cat"), "Thanh toán");
            var prod = new Product(cat.Id, TestDatabase.Unique("pay-prod"), "Phần mềm trả tiền", LicenseType.Subscription);
            v = new ProductVariant(prod.Id, TestDatabase.Unique("PAY"), "12 tháng", price, delivery);
            db.AddRange(cat, prod, v);
            await db.SaveChangesAsync();
        }

        if (keys > 0)
        {
            await using AsyncServiceScope s = factory.Services.CreateAsyncScope();
            await s.ServiceProvider.GetRequiredService<KeyImporter>().RunAsync(v.Sku, Enumerable.Range(0, keys).Select(_ => $"PAY-{Guid.NewGuid():N}"), "t", null, false, default);
        }

        return v;
    }

    private async Task<OrderView> OrderAsync(string token, params (Guid Variant, int Qty)[] items)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { items = items.Select(i => new { variantId = i.Variant, quantity = i.Qty }) }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        HttpResponseMessage res = await factory.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<OrderView>(Json))!;
    }

    private async Task<OrderView> GetOrderAsync(string token, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{code}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await (await factory.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<OrderView>(Json))!;
    }

    private static object Payload(string content, long amount, string? id = null, string transferType = "in") => new
    {
        id = id ?? Random.Shared.NextInt64(1, long.MaxValue).ToString(),
        gateway = "Vietcombank",
        transactionDate = "2026-11-27 12:00:00",
        accountNumber = "0011001234567",
        code = (string?)null,
        content,
        transferType,
        transferAmount = amount,
        referenceCode = "FT" + Random.Shared.Next(100000, 999999),
        description = "",
    };

    private Task<HttpResponseMessage> WebhookAsync(object payload, string secret = ApiFactory.TestSepaySecret, long? unixTime = null, HttpClient? client = null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        string ts = (unixTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/sepay") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add(SepaySignature.SignatureHeader, SepaySignature.Sign(secret, ts, body));
        req.Headers.Add(SepaySignature.TimestampHeader, ts);
        return (client ?? factory.CreateClient()).SendAsync(req);
    }

    private static async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private static async Task<List<Payment>> PaymentsForAsync(string code)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        // Nội dung chuyển khoản giữ nguyên như ngân hàng gửi (có thể chữ thường) ⇒ so không phân biệt hoa thường.
        return await db.Set<Payment>().Where(p => EF.Functions.ILike(p.TransferContent, $"%{code}%")).ToListAsync();
    }

    // ---------- Luồng chính ----------

    [Fact]
    public async Task Exact_payment_fulfils_the_order_and_shows_the_keys()
    {
        ProductVariant v = await VariantAsync(keys: 2);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 2));

        HttpResponseMessage res = await WebhookAsync(Payload($"MBVCB.1 {order.Code.ToLowerInvariant()} chuyen khoan", 2_000_000));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"success\":true", await res.Content.ReadAsStringAsync());
        OrderView after = await GetOrderAsync(token, order.Code);
        Assert.Equal("fulfilled", after.Status);
        Assert.Equal(2, after.Lines[0].Keys.Count);
        Assert.Equal(["pending_payment", "paid", "fulfilled"], after.Timeline.Select(t => t.ToStatus));
        Assert.Equal("webhook", after.Timeline[1].ActorType);
        Payment p = Assert.Single(await PaymentsForAsync(order.Code));
        Assert.Equal(PaymentMatchStatus.Matched, p.MatchStatus);
        Assert.NotNull(p.WebhookEventId);
    }

    [Fact]
    public async Task The_same_webhook_sent_ten_times_records_one_payment()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));
        object payload = Payload(order.Code, 1_000_000, id: Random.Shared.NextInt64(1, long.MaxValue).ToString());

        HttpResponseMessage[] sequential = [await WebhookAsync(payload), await WebhookAsync(payload), await WebhookAsync(payload)];
        HttpResponseMessage[] parallel = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => WebhookAsync(payload)));

        Assert.All(sequential.Concat(parallel), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(await PaymentsForAsync(order.Code));
        Assert.Equal("fulfilled", (await GetOrderAsync(token, order.Code)).Status);
    }

    // ---------- Chữ ký ----------

    [Fact]
    public async Task Forged_or_replayed_webhooks_are_rejected_and_kept_as_evidence()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));

        HttpResponseMessage forged = await WebhookAsync(Payload(order.Code, 1_000_000), secret: "doan-mo");
        HttpResponseMessage replayed = await WebhookAsync(Payload(order.Code, 1_000_000), unixTime: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Empty(await PaymentsForAsync(order.Code));
        Assert.Equal("pending_payment", (await GetOrderAsync(token, order.Code)).Status);
        await using AppDbContext db = TestDatabase.CreateContext();
        List<WebhookEvent> evidence = (await db.Set<WebhookEvent>().Where(e => !e.SignatureValid && e.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(-5)).ToListAsync())
            .Where(e => e.Payload.Contains(order.Code)).ToList();
        Assert.Equal(["bad_signature", "stale_or_bad_timestamp"], evidence.Select(e => e.LastError).Order());
        Assert.All(evidence, e => Assert.Null(e.ProcessedAt));
    }

    // ---------- Tiền lệch ----------

    [Theory]
    [InlineData(999_999, "underpaid", "pending_payment")]
    [InlineData(1_000_500, "overpaid", "fulfilled")]
    public async Task Wrong_amounts_are_classified(long amount, string matchStatus, string orderStatus)
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));

        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(Payload(order.Code, amount))).StatusCode);

        Assert.Equal(matchStatus, server.Infrastructure.Persistence.Conversions.SnakeCaseEnumConverter<PaymentMatchStatus>.ToProvider(
            Assert.Single(await PaymentsForAsync(order.Code)).MatchStatus));
        Assert.Equal(orderStatus, (await GetOrderAsync(token, order.Code)).Status);
    }

    [Fact]
    public async Task Money_without_a_known_order_code_waits_for_an_admin()
    {
        string marker = $"khong ma {Guid.NewGuid():N}";
        string unknown = "DX" + "7K3QH9M2";   // đúng định dạng nhưng gần như chắc chắn không tồn tại
        await SqlAsync($"DELETE FROM sales.order_status_history WHERE false");   // không làm gì — giữ cùng kết nối pool

        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(Payload(marker, 500_000))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(Payload($"{unknown} {marker}x", 500_000))).StatusCode);

        await using AppDbContext db = TestDatabase.CreateContext();
        Payment noCode = await db.Set<Payment>().SingleAsync(p => p.TransferContent == marker);
        Assert.Equal(PaymentMatchStatus.NoOrderCode, noCode.MatchStatus);
        Assert.Null(noCode.OrderId);
        Payment other = await db.Set<Payment>().SingleAsync(p => p.TransferContent == $"{unknown} {marker}x");
        Assert.Contains(other.MatchStatus, new[] { PaymentMatchStatus.UnknownOrder, PaymentMatchStatus.OrderNotPayable, PaymentMatchStatus.Underpaid });
    }

    [Fact]
    public async Task Second_payment_for_a_paid_order_is_not_applied()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));
        await WebhookAsync(Payload(order.Code, 1_000_000));

        await WebhookAsync(Payload(order.Code, 1_000_000));

        List<Payment> payments = await PaymentsForAsync(order.Code);
        Assert.Equal(2, payments.Count);
        Assert.Contains(payments, p => p.MatchStatus == PaymentMatchStatus.OrderNotPayable);
    }

    // ---------- Tiền đến muộn ----------

    private async Task ExpireAsync(string code)
    {
        await SqlAsync($"UPDATE sales.orders SET created_at = created_at - interval '31 minutes', expires_at = created_at - interval '1 minute' WHERE code = '{code}'");
        await factory.Services.GetRequiredService<OrderExpiry>().RunOnceAsync(default);
    }

    [Fact]
    public async Task Late_payment_within_24_hours_reopens_the_order_when_keys_remain()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));
        await ExpireAsync(order.Code);
        Assert.Equal("cancelled", (await GetOrderAsync(token, order.Code)).Status);

        await WebhookAsync(Payload(order.Code, 1_000_000));

        OrderView after = await GetOrderAsync(token, order.Code);
        Assert.Equal("fulfilled", after.Status);
        Assert.Null(after.CancelReason);
        Assert.Single(after.Lines[0].Keys);
        Assert.Contains(after.Timeline, e => e.FromStatus == "cancelled" && e.ToStatus == "paid" && e.Reason == "late_payment");
    }

    [Fact]
    public async Task Late_payment_without_stock_stays_cancelled_for_a_refund()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string late = await TokenAsync();
        OrderView order = await OrderAsync(late, (v.Id, 1));
        await ExpireAsync(order.Code);
        await OrderAsync(await TokenAsync(), (v.Id, 1));        // người khác lấy mất key cuối

        await WebhookAsync(Payload(order.Code, 1_000_000));

        Assert.Equal("cancelled", (await GetOrderAsync(late, order.Code)).Status);
        Assert.Equal(PaymentMatchStatus.LateNoStock, Assert.Single(await PaymentsForAsync(order.Code)).MatchStatus);
    }

    // ---------- Khác ----------

    [Fact]
    public async Task Orders_with_manual_items_stop_at_paid()
    {
        ProductVariant keyed = await VariantAsync(keys: 1);
        ProductVariant manual = await VariantAsync(keys: 0, DeliveryType.Manual, price: 300_000);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (keyed.Id, 1), (manual.Id, 1));

        await WebhookAsync(Payload(order.Code, 1_300_000));

        OrderView after = await GetOrderAsync(token, order.Code);
        Assert.Equal("paid", after.Status);                       // admin giao phần manual
        Assert.Single(after.Lines.Single(l => l.DeliveryType == "license_key").Keys);
    }

    [Fact]
    public async Task Outgoing_transfers_are_acknowledged_but_not_recorded_as_payments()
    {
        string marker = $"chi tien {Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(Payload(marker, 100_000, transferType: "out"))).StatusCode);
        Assert.Empty(await PaymentsForAsync(marker));
    }

    [Fact]
    public async Task Retry_job_processes_events_left_unprocessed()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        string token = await TokenAsync();
        OrderView order = await OrderAsync(token, (v.Id, 1));
        string payload = JsonSerializer.Serialize(Payload(order.Code, 1_000_000));
        // Giả lập: sự kiện đã lưu nhưng API sập trước khi xử lý.
        await SqlAsync($"""
            INSERT INTO payments.webhook_events (id, provider, event_key, signature_valid, payload, attempts, last_error)
            VALUES (gen_random_uuid(), 'sepay', 'retry-{Guid.NewGuid():N}', true, '{payload}', 1, 'TimeoutException: giả lập')
            """);

        // Job xử lý tối đa 50 sự kiện mỗi lượt (cũ nhất trước) ⇒ chạy tới khi hết hàng đợi, như các lượt mỗi phút.
        WebhookMaintenance job = factory.Services.GetRequiredService<WebhookMaintenance>();
        int processed = 0;
        for (int i = 0, n; i < 20 && (n = await job.RetryPendingAsync(default)) > 0; i++)
        {
            processed += n;
        }

        Assert.True(processed >= 1);
        Assert.Equal("fulfilled", (await GetOrderAsync(token, order.Code)).Status);
    }

    [Fact]
    public async Task Admins_see_unresolved_payments_customers_do_not()
    {
        ProductVariant v = await VariantAsync(keys: 1);
        OrderView order = await OrderAsync(await TokenAsync(), (v.Id, 1));
        await WebhookAsync(Payload(order.Code, 1));

        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/payments/unresolved")).StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(admin: true));
        string body = await c.GetStringAsync("/api/admin/payments/unresolved");
        Assert.Contains(order.Code, body);
        Assert.Contains("underpaid", body);
    }
}

public class PaymentWebhookConfigurationTests
{
    private sealed class NoSecret : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Payments:Sepay:WebhookSecret", "");
        }
    }

    private sealed class StrictIps : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Payments:Sepay:CheckIps", "true");   // TestServer không có IP nguồn ⇒ không nằm trong danh sách SePay
        }
    }

    private static HttpRequestMessage Signed(string secret)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { id = Guid.NewGuid().ToString("N"), transferType = "in", transferAmount = 1, content = "x" });
        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/sepay") { Content = new ByteArrayContent(body) };
        req.Headers.Add(SepaySignature.SignatureHeader, SepaySignature.Sign(secret, ts, body));
        req.Headers.Add(SepaySignature.TimestampHeader, ts);
        return req;
    }

    [Fact]
    public async Task Webhook_is_404_until_a_secret_is_configured()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new NoSecret();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(Signed("bat-ky"))).StatusCode);
    }

    [Fact]
    public async Task Requests_from_ips_outside_the_sepay_list_are_rejected_even_when_signed()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new StrictIps();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(Signed(ApiFactory.TestSepaySecret))).StatusCode);
    }
}
