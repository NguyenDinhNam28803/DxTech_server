using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_Sales_Payments_Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "import_batches",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    inserted_rows = table.Column<int>(type: "integer", nullable: false),
                    duplicate_rows = table.Column<int>(type: "integer", nullable: false),
                    invalid_rows = table.Column<int>(type: "integer", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.CheckConstraint("ck_import_batches_counts", "total_rows >= 0 AND inserted_rows >= 0 AND duplicate_rows >= 0 AND invalid_rows >= 0 AND inserted_rows + duplicate_rows + invalid_rows = total_rows");
                    table.CheckConstraint("ck_import_batches_importer", "source <> 'admin' OR imported_by IS NOT NULL");
                    table.CheckConstraint("ck_import_batches_source", "source IN ('cli', 'admin')");
                    table.CheckConstraint("ck_import_batches_texts", "char_length(file_name) BETWEEN 1 AND 255 AND (note IS NULL OR char_length(note) <= 500)");
                    table.ForeignKey(
                        name: "fk_import_batches_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_batches_users_imported_by",
                        column: x => x.imported_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    customer_email = table.Column<string>(type: "citext", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    vat_info = table.Column<string>(type: "jsonb", nullable: true),
                    subtotal_vnd = table.Column<long>(type: "bigint", nullable: false),
                    discount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    total_vnd = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_amounts", "subtotal_vnd >= 0 AND discount_vnd >= 0 AND discount_vnd <= subtotal_vnd AND total_vnd = subtotal_vnd - discount_vnd AND total_vnd > 0");
                    table.CheckConstraint("ck_orders_cancel_reason", "cancel_reason IS NULL OR cancel_reason IN ('expired', 'customer', 'admin')");
                    table.CheckConstraint("ck_orders_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_orders_code", "code ~ '^DX[0-9A-HJKMNP-TV-Z]{8}$'");
                    table.CheckConstraint("ck_orders_customer_email", "char_length(customer_email) BETWEEN 3 AND 254");
                    table.CheckConstraint("ck_orders_customer_name", "char_length(customer_name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_orders_expires", "expires_at > created_at");
                    table.CheckConstraint("ck_orders_fulfilled_at", "status <> 'fulfilled' OR fulfilled_at IS NOT NULL");
                    table.CheckConstraint("ck_orders_note", "note IS NULL OR char_length(note) <= 1000");
                    table.CheckConstraint("ck_orders_paid_at", "status NOT IN ('paid', 'fulfilled', 'refunded') OR paid_at IS NOT NULL");
                    table.CheckConstraint("ck_orders_refunded_at", "(status = 'refunded') = (refunded_at IS NOT NULL)");
                    table.CheckConstraint("ck_orders_status", "status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded')");
                    table.CheckConstraint("ck_orders_vat_info", "vat_info IS NULL OR jsonb_typeof(vat_info) = 'object'");
                    table.ForeignKey(
                        name: "fk_orders_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    event_key = table.Column<string>(type: "text", nullable: false),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    remote_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => x.id);
                    table.CheckConstraint("ck_webhook_events_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_webhook_events_event_key", "char_length(event_key) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_webhook_events_headers", "headers IS NULL OR jsonb_typeof(headers) = 'object'");
                    table.CheckConstraint("ck_webhook_events_payload", "jsonb_typeof(payload) IN ('object', 'array')");
                    table.CheckConstraint("ck_webhook_events_processed", "processed_at IS NULL OR signature_valid");
                    table.CheckConstraint("ck_webhook_events_provider", "provider IN ('sepay', 'payos', 'manual')");
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "text", nullable: false),
                    variant_name_snapshot = table.Column<string>(type: "text", nullable: false),
                    sku_snapshot = table.Column<string>(type: "text", nullable: false),
                    delivery_type = table.Column<string>(type: "text", nullable: false),
                    unit_price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total_vnd = table.Column<long>(type: "bigint", nullable: false),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_delivery_type", "delivery_type IN ('license_key', 'account', 'manual')");
                    table.CheckConstraint("ck_order_items_line_total", "line_total_vnd = unit_price_vnd * quantity");
                    table.CheckConstraint("ck_order_items_price", "unit_price_vnd >= 0");
                    table.CheckConstraint("ck_order_items_quantity", "quantity BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_order_items_snapshots", "char_length(product_name_snapshot) BETWEEN 1 AND 200 AND char_length(variant_name_snapshot) BETWEEN 1 AND 200 AND char_length(sku_snapshot) BETWEEN 1 AND 64");
                    table.ForeignKey(
                        name: "fk_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_items_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_type", "actor_type IN ('customer', 'admin', 'system', 'webhook')");
                    table.CheckConstraint("ck_order_status_history_reason", "reason IS NULL OR char_length(reason) <= 200");
                    table.CheckConstraint("ck_order_status_history_statuses", "to_status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded') AND (from_status IS NULL OR from_status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'refunded'))");
                    table.CheckConstraint("ck_order_status_history_transition", "from_status IS DISTINCT FROM to_status");
                    table.ForeignKey(
                        name: "fk_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_txn_id = table.Column<string>(type: "text", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    transfer_content = table.Column<string>(type: "text", nullable: false),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    match_status = table.Column<string>(type: "text", nullable: false),
                    webhook_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount_vnd > 0");
                    table.CheckConstraint("ck_payments_match_status", "match_status IN ('matched', 'underpaid', 'overpaid', 'no_order_code', 'unknown_order', 'order_not_payable', 'late_no_stock')");
                    table.CheckConstraint("ck_payments_order_link", "(order_id IS NULL) = (match_status IN ('no_order_code', 'unknown_order'))");
                    table.CheckConstraint("ck_payments_provider", "provider IN ('sepay', 'payos', 'manual')");
                    table.CheckConstraint("ck_payments_resolved", "(resolved_at IS NULL) = (resolved_by IS NULL)");
                    table.CheckConstraint("ck_payments_texts", "char_length(provider_txn_id) BETWEEN 1 AND 200 AND char_length(transfer_content) <= 500 AND (bank_reference IS NULL OR char_length(bank_reference) <= 100) AND (resolution_note IS NULL OR char_length(resolution_note) <= 1000)");
                    table.ForeignKey(
                        name: "fk_payments_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_users_resolved_by",
                        column: x => x.resolved_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_webhook_events_webhook_event_id",
                        column: x => x.webhook_event_id,
                        principalSchema: "payments",
                        principalTable: "webhook_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "license_keys",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_version = table.Column<short>(type: "smallint", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_hint = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_keys", x => x.id);
                    table.CheckConstraint("ck_license_keys_assigned", "status NOT IN ('reserved', 'delivered') OR (order_item_id IS NOT NULL AND reserved_at IS NOT NULL)");
                    table.CheckConstraint("ck_license_keys_available", "status <> 'available' OR (order_item_id IS NULL AND reserved_at IS NULL AND delivered_at IS NULL)");
                    table.CheckConstraint("ck_license_keys_ciphertext", "octet_length(key_encrypted) >= 29");
                    table.CheckConstraint("ck_license_keys_delivered", "status <> 'delivered' OR delivered_at IS NOT NULL");
                    table.CheckConstraint("ck_license_keys_hash_len", "octet_length(key_hash) = 32");
                    table.CheckConstraint("ck_license_keys_hint", "char_length(key_hint) BETWEEN 1 AND 8");
                    table.CheckConstraint("ck_license_keys_revoked", "(status = 'revoked') = (revoked_reason IS NOT NULL)");
                    table.CheckConstraint("ck_license_keys_status", "status IN ('available', 'reserved', 'delivered', 'revoked')");
                    table.CheckConstraint("ck_license_keys_version", "key_version > 0");
                    table.ForeignKey(
                        name: "fk_license_keys_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "inventory",
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_license_keys_order_items_order_item_id",
                        column: x => x.order_item_id,
                        principalSchema: "sales",
                        principalTable: "order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_license_keys_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_imported_by",
                schema: "inventory",
                table: "import_batches",
                column: "imported_by",
                filter: "imported_by IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_variant_created",
                schema: "inventory",
                table: "import_batches",
                columns: new[] { "variant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_license_keys_available",
                schema: "inventory",
                table: "license_keys",
                columns: new[] { "variant_id", "created_at" },
                filter: "status = 'available'");

            migrationBuilder.CreateIndex(
                name: "ix_license_keys_batch_id",
                schema: "inventory",
                table: "license_keys",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_license_keys_order_item",
                schema: "inventory",
                table: "license_keys",
                column: "order_item_id",
                filter: "order_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_license_keys_key_hash",
                schema: "inventory",
                table: "license_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_items_variant_id",
                schema: "sales",
                table: "order_items",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_order_items_order_variant",
                schema: "sales",
                table: "order_items",
                columns: new[] { "order_id", "variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_id",
                schema: "sales",
                table: "order_status_history",
                columns: new[] { "order_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_expires",
                schema: "sales",
                table: "orders",
                column: "expires_at",
                filter: "status = 'pending_payment'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_created",
                schema: "sales",
                table: "orders",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_created",
                schema: "sales",
                table: "orders",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_orders_code",
                schema: "sales",
                table: "orders",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_orders_user_idempotency",
                schema: "sales",
                table: "orders",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_order_id",
                schema: "payments",
                table: "payments",
                column: "order_id",
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_resolved_by",
                schema: "payments",
                table: "payments",
                column: "resolved_by",
                filter: "resolved_by IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_unresolved",
                schema: "payments",
                table: "payments",
                column: "created_at",
                filter: "match_status <> 'matched' AND resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payments_provider_txn",
                schema: "payments",
                table: "payments",
                columns: new[] { "provider", "provider_txn_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payments_webhook_event_id",
                schema: "payments",
                table: "payments",
                column: "webhook_event_id",
                unique: true,
                filter: "webhook_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_events_received_at",
                schema: "payments",
                table: "webhook_events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_events_unprocessed",
                schema: "payments",
                table: "webhook_events",
                column: "received_at",
                filter: "processed_at IS NULL AND signature_valid");

            migrationBuilder.CreateIndex(
                name: "ux_webhook_events_provider_key",
                schema: "payments",
                table: "webhook_events",
                columns: new[] { "provider", "event_key" },
                unique: true);

            // Quyền DB theo bảng (thiết kế PR-9 mục A5): chứng từ không bị xoá, lịch sử và payment chỉ thêm.
            // roles.sql (mục 5b) lặp lại đúng các lệnh này mỗi lần deploy vì mục 5 của nó cấp lại toàn bộ quyền.
            migrationBuilder.Sql(server.Modules.Sales.Persistence.Phase3Privileges.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_keys",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "order_status_history",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "import_batches",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "order_items",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "webhook_events",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "sales");
        }
    }
}
