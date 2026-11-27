using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Domain;
using server.Modules.Identity.Domain;
using server.Modules.Inventory.Domain;
using server.Modules.Payments.Domain;
using server.Modules.Sales.Domain;

namespace server.IntegrationTests.Phase3;

/// <summary>
/// Migration Phase3_Sales_Payments_Inventory trên PostgreSQL thật (PR-9, thiết kế mục A4/A6):
/// mỗi ràng buộc CHECK/UNIQUE có ít nhất một ca vi phạm, ánh xạ EF đọc/ghi đủ cột, xmin, cấp key SKIP LOCKED.
/// Ca vi phạm chạy trong transaction rồi ROLLBACK ⇒ dữ liệu mẫu dùng lại được, chạy song song an toàn.
/// </summary>
public class Phase3SchemaTests : IAsyncLifetime
{
    private static readonly string FakeHash = "AQAAAAIAAYagAAAAE" + new string('x', 60);

    private Fixture _f = null!;

    private sealed record Fixture(Guid User, Guid Variant, Guid Order, Guid Item, Guid Event, Guid InvalidEvent, Guid Payment, Guid Batch, Guid Key);

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureMigratedAsync();
        _f = await CreateFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Fixture> CreateFixtureAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using AppDbContext db = TestDatabase.CreateContext();

        var user = new User($"{TestDatabase.Unique("buyer")}@dxtechs.test", "Người Mua", FakeHash, UserRole.Customer);
        var category = new Category(TestDatabase.Unique("p3cat"), "Phase 3");
        var product = new Product(category.Id, TestDatabase.Unique("p3prod"), "Phần mềm Phase 3", LicenseType.Subscription);
        var variant = new ProductVariant(product.Id, TestDatabase.Unique("P3SKU"), "12 tháng", 1_290_000, DeliveryType.LicenseKey);
        db.AddRange(user, category, product, variant);
        await db.SaveChangesAsync();

        var order = new Order(user.Id, Guid.NewGuid(), user.Email, user.FullName, now);
        OrderItem item = order.AddItem(variant.Id, product.Name, variant.Name, variant.Sku, ItemDeliveryType.LicenseKey, variant.PriceVnd, 2);
        var history = new OrderStatusHistory(order.Id, null, OrderStatus.PendingPayment, OrderActorType.Customer, now, user.Id);
        var evt = new WebhookEvent(PaymentProvider.Sepay, TestDatabase.Unique("evt"), true, """{"id":1}""", now, IPAddress.Parse("172.236.138.20"));
        var bad = new WebhookEvent(PaymentProvider.Sepay, TestDatabase.Unique("bad"), false, """{"id":2}""", now);
        var payment = new Payment(PaymentProvider.Sepay, TestDatabase.Unique("txn"), order.Code, order.TotalVnd, now, order.Id,
            PaymentMatchStatus.Matched, now, evt.Id, "FT2633");
        var batch = new ImportBatch(variant.Id, ImportSource.Cli, "keys.csv", now);
        batch.Record(inserted: 1, duplicates: 0, invalid: 0);
        var key = new LicenseKey(variant.Id, batch.Id, RandomNumberGenerator.GetBytes(48), 1, RandomNumberGenerator.GetBytes(32), "Q7XZ", now);

        db.AddRange(order, history, evt, bad, payment, batch, key);
        await db.SaveChangesAsync();
        return new Fixture(user.Id, variant.Id, order.Id, item.Id, evt.Id, bad.Id, payment.Id, batch.Id, key.Id);
    }

    private string Sql(string template) => template
        .Replace("{user}", _f.User.ToString()).Replace("{variant}", _f.Variant.ToString())
        .Replace("{order}", _f.Order.ToString()).Replace("{item}", _f.Item.ToString())
        .Replace("{event}", _f.Event.ToString()).Replace("{bad_event}", _f.InvalidEvent.ToString())
        .Replace("{payment}", _f.Payment.ToString()).Replace("{batch}", _f.Batch.ToString()).Replace("{key}", _f.Key.ToString());

    /// <summary>Chạy SQL trong transaction rồi ROLLBACK; trả lỗi PostgreSQL nếu có.</summary>
    private async Task<PostgresException?> TryInTransactionAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(Sql(sql), conn, tx);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    // ---------- Ràng buộc: mỗi CHECK/UNIQUE một ca vi phạm ----------

    [Theory]
    // sales.orders
    [InlineData("UPDATE sales.orders SET code = 'DX-7K3QH9M2' WHERE id = '{order}'", "ck_orders_code")]
    [InlineData("UPDATE sales.orders SET code = 'DX7K3QH9MI' WHERE id = '{order}'", "ck_orders_code")]
    [InlineData("UPDATE sales.orders SET status = 'shipped' WHERE id = '{order}'", "ck_orders_status")]
    [InlineData("UPDATE sales.orders SET total_vnd = total_vnd + 1 WHERE id = '{order}'", "ck_orders_amounts")]
    [InlineData("UPDATE sales.orders SET discount_vnd = subtotal_vnd + 1, total_vnd = -1 WHERE id = '{order}'", "ck_orders_amounts")]
    [InlineData("UPDATE sales.orders SET subtotal_vnd = 0, total_vnd = 0 WHERE id = '{order}'", "ck_orders_amounts")]
    [InlineData("UPDATE sales.orders SET expires_at = created_at WHERE id = '{order}'", "ck_orders_expires")]
    [InlineData("UPDATE sales.orders SET status = 'paid' WHERE id = '{order}'", "ck_orders_paid_at")]
    [InlineData("UPDATE sales.orders SET status = 'fulfilled', paid_at = now() WHERE id = '{order}'", "ck_orders_fulfilled_at")]
    [InlineData("UPDATE sales.orders SET status = 'cancelled' WHERE id = '{order}'", "ck_orders_cancelled")]
    [InlineData("UPDATE sales.orders SET cancelled_at = now(), cancel_reason = 'expired' WHERE id = '{order}'", "ck_orders_cancelled")]
    [InlineData("UPDATE sales.orders SET status = 'cancelled', cancelled_at = now(), cancel_reason = 'bored' WHERE id = '{order}'", "ck_orders_cancel_reason")]
    [InlineData("UPDATE sales.orders SET refunded_at = now() WHERE id = '{order}'", "ck_orders_refunded_at")]
    [InlineData("UPDATE sales.orders SET vat_info = '\"chuoi\"' WHERE id = '{order}'", "ck_orders_vat_info")]
    [InlineData("UPDATE sales.orders SET note = repeat('x', 1001) WHERE id = '{order}'", "ck_orders_note")]
    // sales.order_items
    [InlineData("UPDATE sales.order_items SET quantity = 21, line_total_vnd = unit_price_vnd * 21 WHERE id = '{item}'", "ck_order_items_quantity")]
    [InlineData("UPDATE sales.order_items SET line_total_vnd = line_total_vnd + 1 WHERE id = '{item}'", "ck_order_items_line_total")]
    [InlineData("UPDATE sales.order_items SET delivery_type = 'email' WHERE id = '{item}'", "ck_order_items_delivery_type")]
    [InlineData("UPDATE sales.order_items SET sku_snapshot = '' WHERE id = '{item}'", "ck_order_items_snapshots")]
    [InlineData("""
        INSERT INTO sales.order_items (id, order_id, variant_id, product_name_snapshot, variant_name_snapshot, sku_snapshot,
                                       delivery_type, unit_price_vnd, quantity, line_total_vnd)
        VALUES (gen_random_uuid(), '{order}', '{variant}', 'P', 'V', 'S', 'license_key', 100, 1, 100)
        """, "ux_order_items_order_variant")]
    // sales.order_status_history
    [InlineData("INSERT INTO sales.order_status_history (order_id, from_status, to_status, actor_type) VALUES ('{order}', 'paid', 'paid', 'system')", "ck_order_status_history_transition")]
    [InlineData("INSERT INTO sales.order_status_history (order_id, to_status, actor_type) VALUES ('{order}', 'paid', 'robot')", "ck_order_status_history_actor_type")]
    [InlineData("INSERT INTO sales.order_status_history (order_id, to_status, actor_type) VALUES ('{order}', 'shipped', 'system')", "ck_order_status_history_statuses")]
    // payments.webhook_events
    [InlineData("UPDATE payments.webhook_events SET processed_at = now() WHERE id = '{bad_event}'", "ck_webhook_events_processed")]
    [InlineData("UPDATE payments.webhook_events SET payload = '\"chuoi\"' WHERE id = '{event}'", "ck_webhook_events_payload")]
    [InlineData("UPDATE payments.webhook_events SET provider = 'momo' WHERE id = '{event}'", "ck_webhook_events_provider")]
    [InlineData("""
        INSERT INTO payments.webhook_events (id, provider, event_key, signature_valid, payload)
        SELECT gen_random_uuid(), provider, event_key, true, '{}' FROM payments.webhook_events WHERE id = '{event}'
        """, "ux_webhook_events_provider_key")]
    // payments.payments
    [InlineData("""
        INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at)
        VALUES (gen_random_uuid(), NULL, 'sepay', 'tx-' || gen_random_uuid(), 'x', 100, 'matched', now())
        """, "ck_payments_order_link")]
    [InlineData("""
        INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at)
        VALUES (gen_random_uuid(), '{order}', 'sepay', 'tx-' || gen_random_uuid(), 'x', 100, 'no_order_code', now())
        """, "ck_payments_order_link")]
    [InlineData("""
        INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at)
        VALUES (gen_random_uuid(), '{order}', 'sepay', 'tx-' || gen_random_uuid(), 'x', 0, 'underpaid', now())
        """, "ck_payments_amount")]
    [InlineData("UPDATE payments.payments SET resolved_at = now() WHERE id = '{payment}'", "ck_payments_resolved")]
    [InlineData("""
        INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at)
        SELECT gen_random_uuid(), order_id, provider, provider_txn_id, 'x', 100, 'overpaid', now() FROM payments.payments WHERE id = '{payment}'
        """, "ux_payments_provider_txn")]
    // inventory.import_batches
    [InlineData("UPDATE inventory.import_batches SET source = 'admin' WHERE id = '{batch}'", "ck_import_batches_importer")]
    [InlineData("UPDATE inventory.import_batches SET total_rows = total_rows + 1 WHERE id = '{batch}'", "ck_import_batches_counts")]
    // inventory.license_keys
    [InlineData("UPDATE inventory.license_keys SET order_item_id = '{item}' WHERE id = '{key}'", "ck_license_keys_available")]
    [InlineData("UPDATE inventory.license_keys SET status = 'reserved', reserved_at = now() WHERE id = '{key}'", "ck_license_keys_assigned")]
    [InlineData("UPDATE inventory.license_keys SET status = 'delivered', order_item_id = '{item}', reserved_at = now() WHERE id = '{key}'", "ck_license_keys_delivered")]
    [InlineData("UPDATE inventory.license_keys SET status = 'revoked' WHERE id = '{key}'", "ck_license_keys_revoked")]
    [InlineData("UPDATE inventory.license_keys SET key_hash = '\\x00'::bytea WHERE id = '{key}'", "ck_license_keys_hash_len")]
    [InlineData("UPDATE inventory.license_keys SET key_encrypted = '\\x0102'::bytea WHERE id = '{key}'", "ck_license_keys_ciphertext")]
    [InlineData("UPDATE inventory.license_keys SET key_version = 0 WHERE id = '{key}'", "ck_license_keys_version")]
    [InlineData("""
        INSERT INTO inventory.license_keys (id, variant_id, batch_id, key_encrypted, key_version, key_hash, key_hint, status)
        SELECT gen_random_uuid(), variant_id, batch_id, key_encrypted, 1, key_hash, 'DUP1', 'available'
          FROM inventory.license_keys WHERE id = '{key}'
        """, "ux_license_keys_key_hash")]
    public async Task Database_rejects_invalid_rows(string sql, string constraint)
    {
        PostgresException? ex = await TryInTransactionAsync(sql);
        Assert.NotNull(ex);
        Assert.Equal(constraint, ex!.ConstraintName);
    }

    // ---------- Chuyển đổi hợp lệ: ràng buộc không được chặn nhầm ----------

    [Theory]
    // Luồng chính: chờ → đã trả → đã giao.
    [InlineData("UPDATE sales.orders SET status = 'paid', paid_at = now() WHERE id = '{order}'; UPDATE sales.orders SET status = 'fulfilled', fulfilled_at = now() WHERE id = '{order}'")]
    // Tiền đến muộn: huỷ vì hết hạn → mở lại (xoá thông tin huỷ) → đã trả.
    [InlineData("UPDATE sales.orders SET status = 'cancelled', cancelled_at = now(), cancel_reason = 'expired' WHERE id = '{order}'; UPDATE sales.orders SET status = 'paid', paid_at = now(), cancelled_at = NULL, cancel_reason = NULL WHERE id = '{order}'")]
    // Key: giữ → giao → bị hãng thu hồi (vẫn giữ liên kết đơn để truy vết).
    [InlineData("UPDATE inventory.license_keys SET status = 'reserved', order_item_id = '{item}', reserved_at = now() WHERE id = '{key}'; UPDATE inventory.license_keys SET status = 'delivered', delivered_at = now() WHERE id = '{key}'; UPDATE inventory.license_keys SET status = 'revoked', revoked_reason = 'vendor_revoked' WHERE id = '{key}'")]
    // Tiền không có mã đơn: lưu với order_id NULL; admin xử lý xong.
    [InlineData("INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at, resolved_at, resolved_by, resolution_note) VALUES (gen_random_uuid(), NULL, 'sepay', 'tx-' || gen_random_uuid(), 'chuyen tien', 500000, 'no_order_code', now(), now(), '{user}', 'Đã hoàn tiền')")]
    public async Task Valid_transitions_are_accepted(string sql)
        => Assert.Null(await TryInTransactionAsync(sql));

    // ---------- Ánh xạ EF ----------

    [Fact]
    public async Task Entities_round_trip_through_EF()
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        Order order = await db.Set<Order>().Include(o => o.Items).SingleAsync(o => o.Id == _f.Order);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(2_580_000, order.TotalVnd);
        Assert.True(OrderCode.IsValid(order.Code));
        OrderItem item = Assert.Single(order.Items);
        Assert.Equal(ItemDeliveryType.LicenseKey, item.DeliveryType);

        WebhookEvent evt = await db.Set<WebhookEvent>().SingleAsync(e => e.Id == _f.Event);
        Assert.Equal(IPAddress.Parse("172.236.138.20"), evt.RemoteIp);
        Payment payment = await db.Set<Payment>().SingleAsync(p => p.Id == _f.Payment);
        Assert.Equal(PaymentMatchStatus.Matched, payment.MatchStatus);
        Assert.Equal(order.Id, payment.OrderId);

        LicenseKey key = await db.Set<LicenseKey>().SingleAsync(k => k.Id == _f.Key);
        Assert.Equal(LicenseKeyStatus.Available, key.Status);
        Assert.Equal(32, key.KeyHash.Length);

        OrderStatusHistory h = await db.Set<OrderStatusHistory>().SingleAsync(x => x.OrderId == _f.Order);
        Assert.True(h.Id > 0);
        Assert.Null(h.FromStatus);
    }

    [Fact]
    public async Task Concurrent_writes_to_the_same_order_are_detected_by_xmin()
    {
        // Dùng đơn riêng để không ảnh hưởng ca khác.
        Guid orderId;
        await using (AppDbContext setup = TestDatabase.CreateContext())
        {
            var o = new Order(_f.User, Guid.NewGuid(), "x@dxtechs.test", "X", DateTimeOffset.UtcNow);
            o.AddItem(_f.Variant, "P", "V", "S", ItemDeliveryType.LicenseKey, 1000, 1);
            setup.Add(o);
            await setup.SaveChangesAsync();
            orderId = o.Id;
        }

        await using AppDbContext webhook = TestDatabase.CreateContext();
        await using AppDbContext expiryJob = TestDatabase.CreateContext();
        Order a = await webhook.Set<Order>().SingleAsync(o => o.Id == orderId);
        Order b = await expiryJob.Set<Order>().SingleAsync(o => o.Id == orderId);

        a.Status = OrderStatus.Paid;
        a.PaidAt = DateTimeOffset.UtcNow;
        await webhook.SaveChangesAsync();

        b.Status = OrderStatus.Cancelled;
        b.CancelledAt = DateTimeOffset.UtcNow;
        b.CancelReason = OrderCancelReason.Expired;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => expiryJob.SaveChangesAsync());
    }

    // ---------- Cấp key đồng thời ----------

    [Fact]
    public async Task Two_concurrent_reservations_never_take_the_same_key()
    {
        // Gói riêng có đúng 2 key.
        Guid variantId;
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            var category = new Category(TestDatabase.Unique("lockcat"), "Khoá");
            var product = new Product(category.Id, TestDatabase.Unique("lockprod"), "Khoá", LicenseType.Perpetual);
            var variant = new ProductVariant(product.Id, TestDatabase.Unique("LOCK"), "Gói", 100_000, DeliveryType.LicenseKey);
            var batch = new ImportBatch(variant.Id, ImportSource.Cli, "lock.csv", DateTimeOffset.UtcNow);
            batch.Record(2, 0, 0);
            db.AddRange(category, product, variant, batch,
                new LicenseKey(variant.Id, batch.Id, RandomNumberGenerator.GetBytes(48), 1, RandomNumberGenerator.GetBytes(32), "AAA1", DateTimeOffset.UtcNow),
                new LicenseKey(variant.Id, batch.Id, RandomNumberGenerator.GetBytes(48), 1, RandomNumberGenerator.GetBytes(32), "BBB2", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            variantId = variant.Id;
        }

        string pick = $"""
            SELECT id FROM inventory.license_keys
             WHERE variant_id = '{variantId}' AND status = 'available'
             ORDER BY created_at LIMIT 1
             FOR UPDATE SKIP LOCKED
            """;

        await using var c1 = new NpgsqlConnection(ApiFactory.ConnectionString);
        await using var c2 = new NpgsqlConnection(ApiFactory.ConnectionString);
        await c1.OpenAsync();
        await c2.OpenAsync();
        await using NpgsqlTransaction t1 = await c1.BeginTransactionAsync();
        await using NpgsqlTransaction t2 = await c2.BeginTransactionAsync();

        var k1 = (Guid)(await new NpgsqlCommand(pick, c1, t1).ExecuteScalarAsync())!;
        // Giao dịch 1 chưa COMMIT mà giao dịch 2 không phải chờ, và lấy key khác.
        var k2 = (Guid)(await new NpgsqlCommand(pick, c2, t2).ExecuteScalarAsync())!;
        Assert.NotEqual(k1, k2);

        // Hết key: lần lấy thứ ba trả về rỗng ngay (không chờ) ⇒ API trả 409 out_of_stock.
        await using var c3 = new NpgsqlConnection(ApiFactory.ConnectionString);
        await c3.OpenAsync();
        await using NpgsqlTransaction t3 = await c3.BeginTransactionAsync();
        Assert.Null(await new NpgsqlCommand(pick, c3, t3).ExecuteScalarAsync());

        await t1.RollbackAsync();
        await t2.RollbackAsync();
        await t3.RollbackAsync();
    }

    // ---------- Index ----------

    [Theory]
    [InlineData("sales", "orders", "ux_orders_code", true, null)]
    [InlineData("sales", "orders", "ux_orders_user_idempotency", true, null)]
    [InlineData("sales", "orders", "ix_orders_pending_expires", false, "(status = 'pending_payment'::text)")]
    [InlineData("sales", "orders", "ix_orders_status_created", false, null)]
    [InlineData("sales", "orders", "ix_orders_user_created", false, null)]
    [InlineData("sales", "order_items", "ux_order_items_order_variant", true, null)]
    [InlineData("sales", "order_status_history", "ix_order_status_history_order_id", false, null)]
    [InlineData("payments", "webhook_events", "ux_webhook_events_provider_key", true, null)]
    [InlineData("payments", "webhook_events", "ix_webhook_events_unprocessed", false, "((processed_at IS NULL) AND signature_valid)")]
    [InlineData("payments", "payments", "ux_payments_provider_txn", true, null)]
    [InlineData("payments", "payments", "ux_payments_webhook_event_id", true, "(webhook_event_id IS NOT NULL)")]
    [InlineData("payments", "payments", "ix_payments_unresolved", false, "((match_status <> 'matched'::text) AND (resolved_at IS NULL))")]
    [InlineData("inventory", "license_keys", "ux_license_keys_key_hash", true, null)]
    [InlineData("inventory", "license_keys", "ix_license_keys_available", false, "(status = 'available'::text)")]
    public async Task Indexes_exist_with_expected_uniqueness_and_filter(string schema, string table, string index, bool unique, string? predicate)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT x.indisunique, pg_get_expr(x.indpred, x.indrelid)
              FROM pg_index x
              JOIN pg_class i ON i.oid = x.indexrelid
              JOIN pg_class t ON t.oid = x.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname = @s AND t.relname = @t AND i.relname = @i
            """, conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("i", index);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"thiếu index {schema}.{index}");
        Assert.Equal(unique, r.GetBoolean(0));
        Assert.Equal(predicate, r.IsDBNull(1) ? null : r.GetString(1));
    }
}
