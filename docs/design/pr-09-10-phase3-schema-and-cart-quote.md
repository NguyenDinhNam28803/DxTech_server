# Thiết kế triển khai PR-9 + PR-10 (Phase 3)

- **Trạng thái:** Đã duyệt (PR #13, 26/11/2026) · PR-9 đã triển khai 27/11 (mục A7) · PR-10 chờ làm
- **Dựa trên:** `docs/database/phase3-schema.md` (đã duyệt 26/11), ADR-013, ADR-014, ADR-001/002/003 (module, DbContext),
  ADR-006 (id, tiền), ADR-007 (concurrency), ADR-009 (role DB), ADR-012 (rate limit)
- **Phạm vi:** PR-9 = cấu trúc dữ liệu (chưa có API). PR-10 = API báo giá giỏ hàng + tồn kho trên trang sản phẩm.
  Không PR nào trong hai PR này nhận tiền hay cấp key.

---

# Phần A — PR-9: Migration Phase 3

## A1. Kết quả sau khi merge

| Hạng mục | Thay đổi |
|---|---|
| Database | 3 schema mới `sales`, `payments`, `inventory`; 7 bảng rỗng; ràng buộc + index như mục A4 |
| Code | 3 module mới chỉ có `Domain/` + `Persistence/` (entity, cấu hình EF), đăng ký vào `AppDbContext` |
| Quyền DB | `dxtechs_app` mất DELETE trên bảng chứng từ; mất UPDATE trên bảng chỉ-thêm (mục A5) |
| API | **Không đổi** — không endpoint mới, web không bị ảnh hưởng |
| Downtime | Không — chỉ tạo bảng mới (< 1 giây), không đụng bảng cũ |
| Quay lại | `Down()` xoá 3 schema (an toàn khi bảng còn rỗng); hoặc giữ nguyên vì code cũ không đọc các bảng này |

## A2. Cấu trúc thư mục

```
server/Modules/
  Sales/      SalesModule.cs · Domain/{Order, OrderItem, OrderStatusHistory, SalesEnums, OrderCode}.cs
              Persistence/{OrderConfiguration, OrderItemConfiguration, OrderStatusHistoryConfiguration}.cs
  Payments/   PaymentsModule.cs · Domain/{Payment, WebhookEvent, PaymentsEnums}.cs · Persistence/…
  Inventory/  InventoryModule.cs · Domain/{LicenseKey, ImportBatch, InventoryEnums}.cs · Persistence/…
```
Quy ước giữ như Catalog/Identity: entity `internal sealed`, id UUID v7 (`UuidV7.NewGuid()`), enum lưu dạng text
(`HasConversion<string>` + CHECK), `xmin` cho `Order` và `LicenseKey`, tên snake_case.
`OrderCode.New()` sinh `DX` + 8 ký tự Crockford base32 từ `RandomNumberGenerator` (40 bit) — viết ở PR-9 để test được
ngay, dùng ở PR-11.

## A3. Migration

Tên: `Phase3_Sales_Payments_Inventory`. SQL sinh bằng `dotnet ef migrations script` lưu ở
`docs/migrations/<timestamp>_Phase3_Sales_Payments_Inventory.sql` để review (như PR-7/PR-8).

## A4. DDL (bản review — EF sinh ra phải khớp)

```sql
CREATE SCHEMA sales; CREATE SCHEMA payments; CREATE SCHEMA inventory;

-- ===================================================================== sales.orders
CREATE TABLE sales.orders (
  id               uuid        NOT NULL,
  code             text        NOT NULL,
  user_id          uuid        NOT NULL,
  idempotency_key  uuid        NOT NULL,
  status           text        NOT NULL DEFAULT 'pending_payment',
  customer_email   citext      NOT NULL,
  customer_name    text        NOT NULL,
  vat_info         jsonb,
  subtotal_vnd     bigint      NOT NULL,
  discount_vnd     bigint      NOT NULL DEFAULT 0,
  total_vnd        bigint      NOT NULL,
  expires_at       timestamptz NOT NULL,
  paid_at          timestamptz, fulfilled_at timestamptz, cancelled_at timestamptz, refunded_at timestamptz,
  cancel_reason    text,
  note             text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_orders PRIMARY KEY (id),
  CONSTRAINT fk_orders_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT,
  CONSTRAINT ck_orders_code          CHECK (code ~ '^DX[0-9A-HJKMNP-TV-Z]{8}$'),
  CONSTRAINT ck_orders_status        CHECK (status IN ('pending_payment','paid','fulfilled','cancelled','refunded')),
  CONSTRAINT ck_orders_amounts       CHECK (subtotal_vnd >= 0 AND discount_vnd >= 0 AND discount_vnd <= subtotal_vnd
                                            AND total_vnd = subtotal_vnd - discount_vnd AND total_vnd > 0),
  CONSTRAINT ck_orders_expires       CHECK (expires_at > created_at),
  CONSTRAINT ck_orders_paid_at       CHECK (status NOT IN ('paid','fulfilled','refunded') OR paid_at IS NOT NULL),
  CONSTRAINT ck_orders_fulfilled_at  CHECK (status <> 'fulfilled' OR fulfilled_at IS NOT NULL),
  CONSTRAINT ck_orders_cancelled     CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)),
  CONSTRAINT ck_orders_refunded_at   CHECK ((status = 'refunded') = (refunded_at IS NOT NULL)),
  CONSTRAINT ck_orders_cancel_reason CHECK (cancel_reason IS NULL OR cancel_reason IN ('expired','customer','admin')),
  CONSTRAINT ck_orders_customer_name CHECK (char_length(customer_name) BETWEEN 1 AND 100),
  CONSTRAINT ck_orders_note          CHECK (note IS NULL OR char_length(note) <= 1000),
  CONSTRAINT ck_orders_vat_info      CHECK (vat_info IS NULL OR jsonb_typeof(vat_info) = 'object')
);
CREATE UNIQUE INDEX ux_orders_code ON sales.orders (code);
CREATE UNIQUE INDEX ux_orders_user_idempotency ON sales.orders (user_id, idempotency_key);
CREATE INDEX ix_orders_user_created ON sales.orders (user_id, created_at DESC);
CREATE INDEX ix_orders_pending_expires ON sales.orders (expires_at) WHERE status = 'pending_payment';
CREATE INDEX ix_orders_status_created ON sales.orders (status, created_at DESC, id DESC);

-- ===================================================================== sales.order_items
CREATE TABLE sales.order_items (
  id uuid NOT NULL, order_id uuid NOT NULL, variant_id uuid NOT NULL,
  product_name_snapshot text NOT NULL, variant_name_snapshot text NOT NULL, sku_snapshot text NOT NULL,
  delivery_type text NOT NULL, unit_price_vnd bigint NOT NULL, quantity integer NOT NULL,
  line_total_vnd bigint NOT NULL, fulfilled_at timestamptz,
  CONSTRAINT pk_order_items PRIMARY KEY (id),
  CONSTRAINT fk_order_items_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT,
  CONSTRAINT fk_order_items_product_variants_variant_id FOREIGN KEY (variant_id)
             REFERENCES catalog.product_variants (id) ON DELETE RESTRICT,
  CONSTRAINT ck_order_items_delivery_type CHECK (delivery_type IN ('license_key','account','manual')),
  CONSTRAINT ck_order_items_quantity      CHECK (quantity BETWEEN 1 AND 20),
  CONSTRAINT ck_order_items_price         CHECK (unit_price_vnd >= 0),
  CONSTRAINT ck_order_items_line_total    CHECK (line_total_vnd = unit_price_vnd * quantity),
  CONSTRAINT ck_order_items_snapshots     CHECK (char_length(product_name_snapshot) BETWEEN 1 AND 200
                                                 AND char_length(variant_name_snapshot) BETWEEN 1 AND 200
                                                 AND char_length(sku_snapshot) BETWEEN 1 AND 64)
);
CREATE UNIQUE INDEX ux_order_items_order_variant ON sales.order_items (order_id, variant_id);
CREATE INDEX ix_order_items_variant_id ON sales.order_items (variant_id);

-- ===================================================================== sales.order_status_history
CREATE TABLE sales.order_status_history (
  id bigint GENERATED ALWAYS AS IDENTITY, order_id uuid NOT NULL,
  from_status text, to_status text NOT NULL, actor_type text NOT NULL, actor_user_id uuid, reason text,
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_order_status_history PRIMARY KEY (id),
  CONSTRAINT fk_order_status_history_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT,
  CONSTRAINT ck_order_status_history_statuses CHECK (to_status IN ('pending_payment','paid','fulfilled','cancelled','refunded')
             AND (from_status IS NULL OR from_status IN ('pending_payment','paid','fulfilled','cancelled','refunded'))),
  CONSTRAINT ck_order_status_history_transition CHECK (from_status IS DISTINCT FROM to_status),
  CONSTRAINT ck_order_status_history_actor_type CHECK (actor_type IN ('customer','admin','system','webhook')),
  CONSTRAINT ck_order_status_history_reason CHECK (reason IS NULL OR char_length(reason) <= 200)
);
CREATE INDEX ix_order_status_history_order_id ON sales.order_status_history (order_id, id);

-- ===================================================================== payments.webhook_events
CREATE TABLE payments.webhook_events (
  id uuid NOT NULL, provider text NOT NULL, event_key text NOT NULL, signature_valid boolean NOT NULL,
  remote_ip inet, payload jsonb NOT NULL, headers jsonb,
  received_at timestamptz NOT NULL DEFAULT now(), processed_at timestamptz,
  attempts integer NOT NULL DEFAULT 0, last_error text,
  CONSTRAINT pk_webhook_events PRIMARY KEY (id),
  CONSTRAINT ck_webhook_events_provider  CHECK (provider IN ('sepay','payos','manual')),
  CONSTRAINT ck_webhook_events_event_key CHECK (char_length(event_key) BETWEEN 1 AND 200),
  CONSTRAINT ck_webhook_events_attempts  CHECK (attempts >= 0),
  CONSTRAINT ck_webhook_events_headers   CHECK (headers IS NULL OR jsonb_typeof(headers) = 'object'),
  CONSTRAINT ck_webhook_events_processed CHECK (processed_at IS NULL OR signature_valid)
);
CREATE UNIQUE INDEX ux_webhook_events_provider_key ON payments.webhook_events (provider, event_key);
CREATE INDEX ix_webhook_events_unprocessed ON payments.webhook_events (received_at)
  WHERE processed_at IS NULL AND signature_valid;
CREATE INDEX ix_webhook_events_received_at ON payments.webhook_events (received_at);   -- dọn 180 ngày, đếm sai chữ ký/ngày

-- ===================================================================== payments.payments
CREATE TABLE payments.payments (
  id uuid NOT NULL, order_id uuid, provider text NOT NULL, provider_txn_id text NOT NULL,
  bank_reference text, transfer_content text NOT NULL, amount_vnd bigint NOT NULL, match_status text NOT NULL,
  webhook_event_id uuid, paid_at timestamptz NOT NULL,
  resolved_at timestamptz, resolved_by uuid, resolution_note text,
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_payments PRIMARY KEY (id),
  CONSTRAINT fk_payments_orders_order_id FOREIGN KEY (order_id) REFERENCES sales.orders (id) ON DELETE RESTRICT,
  CONSTRAINT fk_payments_webhook_events_webhook_event_id FOREIGN KEY (webhook_event_id)
             REFERENCES payments.webhook_events (id) ON DELETE RESTRICT,
  CONSTRAINT fk_payments_users_resolved_by FOREIGN KEY (resolved_by) REFERENCES identity.users (id) ON DELETE RESTRICT,
  CONSTRAINT ck_payments_provider     CHECK (provider IN ('sepay','payos','manual')),
  CONSTRAINT ck_payments_amount       CHECK (amount_vnd > 0),
  CONSTRAINT ck_payments_match_status CHECK (match_status IN ('matched','underpaid','overpaid','no_order_code',
                                             'unknown_order','order_not_payable','late_no_stock')),
  CONSTRAINT ck_payments_order_link   CHECK ((order_id IS NULL) = (match_status IN ('no_order_code','unknown_order'))),
  CONSTRAINT ck_payments_resolved     CHECK ((resolved_at IS NULL) = (resolved_by IS NULL)),
  CONSTRAINT ck_payments_texts        CHECK (char_length(transfer_content) <= 500 AND char_length(provider_txn_id) BETWEEN 1 AND 200)
);
CREATE UNIQUE INDEX ux_payments_provider_txn ON payments.payments (provider, provider_txn_id);
CREATE UNIQUE INDEX ux_payments_webhook_event_id ON payments.payments (webhook_event_id) WHERE webhook_event_id IS NOT NULL;
CREATE INDEX ix_payments_order_id ON payments.payments (order_id) WHERE order_id IS NOT NULL;
CREATE INDEX ix_payments_unresolved ON payments.payments (created_at)
  WHERE match_status <> 'matched' AND resolved_at IS NULL;

-- ===================================================================== inventory.import_batches
CREATE TABLE inventory.import_batches (
  id uuid NOT NULL, variant_id uuid NOT NULL, source text NOT NULL, file_name text NOT NULL,
  total_rows integer NOT NULL, inserted_rows integer NOT NULL, duplicate_rows integer NOT NULL, invalid_rows integer NOT NULL,
  imported_by uuid, note text, created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_import_batches PRIMARY KEY (id),
  CONSTRAINT fk_import_batches_product_variants_variant_id FOREIGN KEY (variant_id)
             REFERENCES catalog.product_variants (id) ON DELETE RESTRICT,
  CONSTRAINT fk_import_batches_users_imported_by FOREIGN KEY (imported_by) REFERENCES identity.users (id) ON DELETE RESTRICT,
  CONSTRAINT ck_import_batches_source   CHECK (source IN ('cli','admin')),
  CONSTRAINT ck_import_batches_importer CHECK (source = 'cli' OR imported_by IS NOT NULL),
  CONSTRAINT ck_import_batches_counts   CHECK (total_rows >= 0 AND inserted_rows >= 0 AND duplicate_rows >= 0 AND invalid_rows >= 0
                                               AND inserted_rows + duplicate_rows + invalid_rows = total_rows),
  CONSTRAINT ck_import_batches_texts    CHECK (char_length(file_name) BETWEEN 1 AND 255 AND (note IS NULL OR char_length(note) <= 500))
);
CREATE INDEX ix_import_batches_variant_created ON inventory.import_batches (variant_id, created_at DESC);

-- ===================================================================== inventory.license_keys
CREATE TABLE inventory.license_keys (
  id uuid NOT NULL, variant_id uuid NOT NULL, batch_id uuid NOT NULL,
  key_encrypted bytea NOT NULL, key_version smallint NOT NULL DEFAULT 1, key_hash bytea NOT NULL, key_hint text NOT NULL,
  status text NOT NULL DEFAULT 'available', order_item_id uuid,
  reserved_at timestamptz, delivered_at timestamptz, revoked_reason text,
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_license_keys PRIMARY KEY (id),
  CONSTRAINT fk_license_keys_product_variants_variant_id FOREIGN KEY (variant_id)
             REFERENCES catalog.product_variants (id) ON DELETE RESTRICT,
  CONSTRAINT fk_license_keys_import_batches_batch_id FOREIGN KEY (batch_id) REFERENCES inventory.import_batches (id) ON DELETE RESTRICT,
  CONSTRAINT fk_license_keys_order_items_order_item_id FOREIGN KEY (order_item_id)
             REFERENCES sales.order_items (id) ON DELETE RESTRICT,
  CONSTRAINT ck_license_keys_status      CHECK (status IN ('available','reserved','delivered','revoked')),
  CONSTRAINT ck_license_keys_version     CHECK (key_version > 0),
  CONSTRAINT ck_license_keys_hash_len    CHECK (octet_length(key_hash) = 32),
  CONSTRAINT ck_license_keys_ciphertext  CHECK (octet_length(key_encrypted) > 28),          -- nonce 12 + tag 16 + ≥ 1 byte
  CONSTRAINT ck_license_keys_hint        CHECK (char_length(key_hint) BETWEEN 1 AND 8),
  CONSTRAINT ck_license_keys_available   CHECK (status <> 'available' OR (order_item_id IS NULL AND reserved_at IS NULL)),
  CONSTRAINT ck_license_keys_assigned    CHECK (status NOT IN ('reserved','delivered') OR (order_item_id IS NOT NULL AND reserved_at IS NOT NULL)),
  CONSTRAINT ck_license_keys_delivered   CHECK (status <> 'delivered' OR delivered_at IS NOT NULL),
  CONSTRAINT ck_license_keys_revoked     CHECK ((status = 'revoked') = (revoked_reason IS NOT NULL))
);
CREATE UNIQUE INDEX ux_license_keys_key_hash ON inventory.license_keys (key_hash);
CREATE INDEX ix_license_keys_available ON inventory.license_keys (variant_id, created_at) WHERE status = 'available';
CREATE INDEX ix_license_keys_order_item ON inventory.license_keys (order_item_id) WHERE order_item_id IS NOT NULL;
CREATE INDEX ix_license_keys_batch_id ON inventory.license_keys (batch_id);
```
Ghi chú review:
- Key bị thu hồi (`revoked`) sau khi đã giao vẫn giữ `order_item_id` ⇒ ràng buộc viết theo từng trạng thái, không dùng một
  biểu thức “gán ⇔ reserved/delivered” như bản thiết kế 3.7.
- Đơn mở lại khi tiền đến muộn: code đặt `cancelled_at = NULL, cancel_reason = NULL` (lịch sử vẫn ghi lần huỷ) ⇒ ràng buộc
  `ck_orders_cancelled` dạng tương đương.
- `ck_webhook_events_processed`: sự kiện sai chữ ký không bao giờ được đánh dấu đã xử lý.
- FK sang `identity.users`, `catalog.product_variants`: chéo schema nhưng cùng DB (ADR-002), cùng owner.

## A5. Quyền DB — phát hiện khi thiết kế

`deploy/postgres/roles.sql` (container `db-roles`) chạy **mỗi lần deploy, trước `migrate`**, và mục 5 của nó
`GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES` cho mọi schema. Nếu chỉ REVOKE trong migration thì **lần deploy sau
quyền DELETE được cấp lại**. Vì vậy quyền làm ở **hai nơi**, đều idempotent:

| Nơi | Khi nào có tác dụng | Nội dung |
|---|---|---|
| Migration (cuối `Up()`) | Ngay lần deploy tạo bảng (db-roles chạy trước, lúc đó bảng chưa có) | `DO $$ … IF EXISTS (role dxtechs_app) THEN REVOKE …` |
| `roles.sql` mục 5b (mới, sau mục 5) | Mọi lần deploy về sau | Cùng các lệnh REVOKE, bọc `WHERE to_regclass('sales.orders') IS NOT NULL` |

```sql
REVOKE DELETE ON sales.orders, sales.order_items, inventory.license_keys, inventory.import_batches FROM dxtechs_app;
REVOKE UPDATE, DELETE, TRUNCATE ON sales.order_status_history FROM dxtechs_app;
REVOKE UPDATE, DELETE, TRUNCATE ON payments.payments FROM dxtechs_app;
GRANT  UPDATE (resolved_at, resolved_by, resolution_note) ON payments.payments TO dxtechs_app;
REVOKE TRUNCATE ON ALL TABLES IN SCHEMA sales, payments, inventory FROM dxtechs_app;
-- webhook_events giữ SELECT, INSERT, UPDATE, DELETE (job dọn 180 ngày)
```

## A6. Kiểm thử PR-9

| Nhóm | Ca kiểm thử |
|---|---|
| Schema (integration, PostgreSQL thật) | Mỗi CHECK có ít nhất 1 ca vi phạm ⇒ `PostgresException` đúng tên ràng buộc (≈ 30 ca: mã đơn có `-`, tổng sai, quantity 21, key `available` có `order_item_id`, payment `matched` không có `order_id`…) |
| Unique | Trùng mã đơn, trùng (user, idempotency_key), trùng (provider, txn), trùng `key_hash` |
| Ánh xạ EF | Ghi/đọc đủ cột mỗi entity; `xmin` đổi sau UPDATE; enum ⇔ text |
| `OrderCode` (unit) | 10.000 mã: đúng regex, không ký tự I/L/O/U, phân bố đều (không ký tự nào > 2× trung bình) |
| Snapshot migration | `dotnet ef migrations has-pending-model-changes` = không |
| Quyền (`test-db-roles.sh`) | App: INSERT/SELECT được; DELETE orders ⇒ lỗi; UPDATE `payments.amount_vnd` ⇒ lỗi; UPDATE `payments.resolved_at` ⇒ được; chạy `roles.sql` lần 2 rồi kiểm lại (bắt lỗi cấp lại quyền) |

---

## A7. Ghi chú triển khai PR-9 (27/11/2026) — khác bản thiết kế

| Điểm | Thiết kế | Triển khai | Lý do |
|---|---|---|---|
| `DEFAULT` cho `status`, `discount_vnd`, `key_version` | Có trong DDL | Không đặt ở DB; entity luôn gán giá trị | Cột enum/số có giá trị mặc định trùng giá trị CLR mặc định gây cảnh báo “sentinel” của EF; mọi đường ghi đều qua entity hoặc ghi tường minh |
| `webhook_events.attempts` | `DEFAULT 0` | Giữ `DEFAULT 0` | Phát hiện khi viết test: INSERT tay thiếu cột này bị lỗi NOT NULL |
| CHECK bổ sung | — | `ck_orders_customer_email` (3–254 ký tự), `ck_webhook_events_payload` (object/array), độ dài `bank_reference` (≤ 100) và `resolution_note` (≤ 1000), `ck_license_keys_available` thêm `delivered_at IS NULL` | Lớp phòng thủ cuối cho dữ liệu nhập tay / module sau |
| Index bổ sung | — | `ix_payments_resolved_by`, `ix_import_batches_imported_by` (partial), `ix_license_keys_batch_id` | Index cho FK sang `identity.users` / lô nhập: tránh quét toàn bảng khi kiểm tra FK |
| FK chéo module | Trong DDL | `HasOne<T>()` không navigation trong `Persistence/` | Domain của module không tham chiếu kiểu nội bộ module khác (test kiến trúc ADR-001) |
| Quyền DB (A5) | Migration + `roles.sql` | `Phase3Privileges.Sql` dùng cho migration; `roles.sql` mục 5b chứa bản giống hệt, test đơn vị so sánh hai bản | Lệch hai bản ⇒ lần deploy thứ hai trả lại quyền DELETE |
| Kiểm thử quyền | Chạy `roles.sql` 2 lần | Thêm: thử phá có chủ đích (bỏ mục 5b ⇒ 6 kiểm tra đỏ); ca “xoá đơn” dùng đơn không có dòng phụ thuộc | Ca “xoá đơn” ban đầu qua vì khoá ngoại chặn, không phải vì quyền — đã sửa |

Kết quả: unit 106 → **131**, integration 162 → **225** (ràng buộc 45 ca, chuyển đổi hợp lệ 4 ca, index 14, EF, xmin, SKIP LOCKED),
`test-db-roles.sh` +19 kiểm tra Phase 3. SQL review: `docs/migrations/20261127034854_Phase3_Sales_Payments_Inventory.sql`.

---

# Phần B — PR-10: Báo giá giỏ hàng + tồn kho

## B1. Mục tiêu
Giỏ hàng lưu ở trình duyệt (localStorage) nhưng **mọi con số đều do server tính lại**: giá, tổng tiền, còn hàng hay không.
Cùng một lõi tính giá sẽ được `POST /api/orders` (PR-11) dùng lại ⇒ giá ở giỏ và giá trên đơn không thể lệch nhau.

## B2. API

### `POST /api/cart/quote` — `[AllowAnonymous]`, rate limit 60/phút/IP
Request:
```json
{ "items": [ { "variantId": "0193…", "quantity": 2 }, { "variantId": "0193…", "quantity": 1 } ] }
```
Kiểm tra đầu vào (400 ProblemDetails, lỗi theo trường): `items` 1–20 phần tử · `quantity` 1–20 · `variantId` là UUID.
`variantId` trùng ⇒ **gộp** số lượng (gộp xong > 20 ⇒ 400).

Response 200:
```json
{
  "lines": [
    { "variantId": "0193…", "productSlug": "microsoft-365-business-standard", "productName": "Microsoft 365 Business Standard",
      "variantName": "12 tháng · 1 người dùng", "sku": "M365-BS-12M-1U", "deliveryType": "license_key",
      "unitPriceVnd": 3290000, "compareAtPriceVnd": 3990000, "quantity": 2, "lineTotalVnd": 6580000,
      "status": "ok", "availableQuantity": null },
    { "variantId": "0193…", "status": "insufficient_stock", "quantity": 5, "availableQuantity": 3, "…": "…" }
  ],
  "subtotalVnd": 6580000, "discountVnd": 0, "totalVnd": 6580000,
  "canCheckout": false,
  "quotedAt": "2026-11-26T05:00:00Z"
}
```

| `status` của dòng | Khi nào | Tính vào tổng? |
|---|---|---|
| `ok` | Gói đang bán, đủ hàng | Có |
| `insufficient_stock` | `license_key`, còn ít hơn số lượng — kèm `availableQuantity` | Không |
| `out_of_stock` | `license_key`, hết key | Không |
| `unavailable` | Gói/sản phẩm/danh mục bị ẩn, xoá, hoặc id không tồn tại (giỏ cũ trong trình duyệt) | Không, thông tin sản phẩm = null |

`canCheckout = true` ⇔ mọi dòng `ok`. Dòng không hợp lệ **không** làm cả request lỗi — giỏ trong trình duyệt có thể cũ,
web cần biết dòng nào để báo khách xoá.

**Không lộ số tồn kho thật:** `availableQuantity` chỉ trả khi thiếu hàng và tối đa 20 (bằng giới hạn mỗi dòng) — đối thủ
không dò được kho bằng cách gọi API.

### `GET /api/products/{slug}` — thêm 1 trường (không phá client cũ)
`variants[].inStock: boolean` — `license_key`: còn ≥ 1 key `available`; `account`/`manual`: luôn `true`.
Danh sách sản phẩm (`GET /api/products`) **không** thêm (giữ ~3,7 ms; xem lại ở PR-C4 nếu cần nhãn “Hết hàng”).

⚠️ Sau khi PR-10 deploy và **chưa nhập key**, mọi gói `license_key` sẽ `inStock = false`. Web hiện tại chưa đọc trường này
nên không thấy khác biệt; PR-C4 phải xử lý hiển thị “Hết hàng” và chỉ mở bán khi đã nhập key.

## B3. Ranh giới module (ADR-001)
Sales **không** truy vấn trực tiếp bảng của Catalog/Inventory — dùng hai interface công khai do module sở hữu dữ liệu cung cấp:

```csharp
// Catalog — một truy vấn cho cả giỏ
public interface IVariantPricing {
    Task<IReadOnlyDictionary<Guid, PurchasableVariant>> GetPurchasableAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}
public sealed record PurchasableVariant(Guid VariantId, string ProductSlug, string ProductName, string VariantName,
    string Sku, string DeliveryType, long PriceVnd, long? CompareAtPriceVnd);

// Inventory — đếm key available theo gói, dùng partial index ix_license_keys_available
public interface IStockQuery {
    Task<IReadOnlyDictionary<Guid, int>> CountAvailableAsync(IReadOnlyCollection<Guid> variantIds, int cap, CancellationToken ct);
}
```
- “Đang bán được” dùng chung điều kiện với trang sản phẩm (`ProductListing.Listed` + gói `IsActive`, chưa xoá mềm).
- Đếm tồn kho: `SELECT variant_id, LEAST(count(*), $cap) … WHERE status = 'available' AND variant_id = ANY($1) GROUP BY variant_id`
  — chỉ quét index một phần (chỉ chứa key còn).
- Lõi tính giá `CartPricing.QuoteAsync(items)` nằm trong Sales, PR-11 gọi lại khi tạo đơn (trong transaction, kèm giữ key).

## B4. Hiệu năng
Một request = 2 truy vấn (biến thể + tồn kho), không phụ thuộc số dòng (≤ 20). Mục tiêu p95 < 20 ms trên VPS hiện tại;
test đo với 2.000 sản phẩm + 50.000 key giả.

## B5. Kiểm thử PR-10

| Ca | Kỳ vọng |
|---|---|
| Giá trong request bị sửa (client gửi thêm `priceVnd`) | Bị bỏ qua — giá luôn từ DB |
| Gói đang bán, đủ hàng | `ok`, tổng đúng |
| Gói/sản phẩm/danh mục ẩn hoặc xoá mềm, id lạ | `unavailable`, không tính tổng, `canCheckout=false` |
| `license_key` còn 3, mua 5 | `insufficient_stock`, `availableQuantity=3` |
| Còn 500, mua 25 | 400 (quantity > 20); còn 500 mua 5 ⇒ `ok`, không trả số 500 |
| `account`/`manual` không có key | `ok` (không cần kho) |
| Trùng variantId 2 dòng | Gộp; gộp > 20 ⇒ 400 |
| 21 dòng, body rỗng, quantity 0 | 400 có lỗi theo trường |
| Rate limit | Lần 61 trong 1 phút ⇒ 429 + Retry-After |
| `GET /api/products/{slug}` | Có `inStock` mỗi gói; client cũ đọc được (trường thêm) |
| Arch test | Sales không tham chiếu entity `internal` của Catalog/Inventory |

---

# Phần C — Triển khai & kiểm tra

| | PR-9 | PR-10 |
|---|---|---|
| Biến `.env` mới | Không | Không |
| Lệnh tay trên VPS trước merge | Backup (như mọi migration) | Không |
| Ảnh hưởng người dùng | Không | Không (web chưa dùng API mới) |
| Kiểm tra sau deploy | `\dt sales.*` … 7 bảng; quyền app (mục A5) | `curl` quote với 1 gói thật ⇒ `out_of_stock` (chưa có key) hoặc `ok` (gói manual) |
| Ước lượng | 1–2 ngày | 1 ngày |

Thứ tự: PR-9 merge + deploy + kiểm tra ⇒ PR-10 (PR-10 phụ thuộc bảng `inventory.license_keys`).
