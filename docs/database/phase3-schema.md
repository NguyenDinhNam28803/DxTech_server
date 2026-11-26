# Thiết kế chi tiết Phase 3 — schema `sales`, `payments`, `inventory`

- **Trạng thái:** Đã duyệt (26/11/2026) · DDL chi tiết + quyền DB: `docs/design/pr-09-10-phase3-schema-and-cart-quote.md`
- **Ngày:** 26/11/2026 · **Thay cho:** *Thiết kế toàn bộ Database v1.0* mục 7–9 (phần Phase 3)
- **Quyết định:** ADR-013 (thanh toán), ADR-014 (mã hoá license key), ADR-006 (id, tiền, thời gian), ADR-007 (concurrency),
  ADR-009 (role DB), ADR-012 (xác thực, rate limit)

## 0. Tóm tắt thay đổi so với Thiết kế DB v1.0

| # | Thay đổi | Lý do |
|---|---|---|
| 1 | `orders.code` = `DX` + 8 ký tự Crockford base32 (`DX7K3QH9M2`) | ADR-013: app ngân hàng bỏ dấu `-` trong nội dung chuyển khoản |
| 2 | **Bắt buộc đăng nhập để đặt hàng** — `orders.user_id NOT NULL` | Key giao qua “Đơn của tôi”, không cần cơ chế link bí mật cho khách vãng lai; nới ra sau chỉ là `DROP NOT NULL` |
| 3 | **Giữ key khi tạo đơn** — `license_keys.status` thêm `reserved` | Khách đã thấy QR thì chắc chắn có key; không xảy ra “đã trả tiền nhưng hết hàng” |
| 4 | `payments.order_id` cho phép NULL + `match_status` thay cho `matched` | Tiền đến không có/sai mã đơn vẫn phải ghi lại; phân loại lý do lệch cho admin |
| 5 | Chuyển đổi `cancelled → paid` (tiền đến muộn ≤ 24 giờ) | ADR-013 mục 4 |
| 6 | `orders.idempotency_key` | Bấm “Đặt hàng” 2 lần / mạng chập chờn không tạo 2 đơn |
| 7 | **Hoãn** `coupons`, `coupon_redemptions`, `refunds` sang Phase 4 | Chưa có màn hình admin để quản lý; giữ `discount_vnd = 0` để thêm sau không đổi công thức tiền |
| 8 | `import_batches` lên Phase 3 (CLI `--import-keys`) | Cần nhập key thật trước khi mở bán; `imported_by` NULL khi nhập bằng CLI |
| 9 | Bảng chỉ-thêm (`order_status_history`, `payments`, `webhook_events`) **thu hồi UPDATE/DELETE** của `dxtechs_app` ở mức quyền DB (trừ cột cần cập nhật) | Lỗi code hoặc SQL injection cũng không sửa/xoá được chứng từ |

## 1. Sơ đồ

```
identity.users ──1:n──► sales.orders ──1:n──► sales.order_items ──1:n──► inventory.license_keys ◄──n:1── inventory.import_batches
                            │  │                     │                         │
                            │  └─1:n─► sales.order_status_history              └── variant_id ──► catalog.product_variants
                            │
                            └─1:n─► payments.payments ◄──1:1── payments.webhook_events
                                        (order_id NULL = tiền chưa khớp đơn)
```
Mọi FK `ON DELETE RESTRICT` (chứng từ không bao giờ xoá cứng). FK chéo schema giữ nguyên vì cùng một database (ADR-002).

## 2. Máy trạng thái đơn hàng

```
                 webhook khớp tiền                 cấp đủ key (tự động) / admin giao (account, manual)
pending_payment ───────────────────► paid ─────────────────────────────────────────────► fulfilled
      │  ▲                             │                                                     │
      │  │ tiền đến muộn ≤ 24h,        │                                                     │
      │  │ đúng số tiền, còn key       └──────────────► refunded (Phase 4) ◄─────────────────┘
      ▼  │
  cancelled   (hết 30 phút · khách huỷ · admin huỷ)
```

| Chuyển | Ai | Điều kiện (UPDATE … WHERE status = …) | Việc làm kèm trong cùng transaction |
|---|---|---|---|
| — → `pending_payment` | khách | đủ key `available` cho mọi dòng `license_key`; ≤ 3 đơn đang chờ / người | Giữ key (`reserved`), ghi lịch sử |
| `pending_payment → paid` | webhook | `amount = total_vnd` | Ghi payment, chuyển key `reserved → delivered` |
| `paid → fulfilled` | hệ thống / admin | mọi dòng đã giao | `order_items.fulfilled_at`, `orders.fulfilled_at` |
| `pending_payment → cancelled` | job / khách / admin | — | Trả key về `available` |
| `cancelled → paid` | webhook | huỷ vì `expired`, ≤ 24 giờ, đúng tiền, giữ lại được đủ key | Như `→ paid`; lý do `late_payment` |
| `paid/fulfilled → refunded` | admin (Phase 4) | — | `refunds`, thu hồi key |

Hai bên cùng đổi trạng thái (webhook ↔ job hết hạn) dùng UPDATE có điều kiện + `xmin` (ADR-007): bên đến sau thấy 0 dòng bị ảnh
hưởng và đi nhánh của mình (webhook đến sau job ⇒ nhánh tiền đến muộn).

## 3. Bảng

### 3.1 `sales.orders`
| Cột | Kiểu | Null / mặc định | Ràng buộc | Ghi chú |
|---|---|---|---|---|
| id | uuid | NOT NULL | PK | UUID v7 |
| code | text | NOT NULL | UQ · CHECK `^DX[0-9A-HJKMNP-TV-Z]{8}$` | 40 bit ngẫu nhiên; trùng ⇒ sinh lại (xác suất ~10⁻⁸ ở 5.000 đơn) |
| user_id | uuid | NOT NULL | FK → identity.users | |
| idempotency_key | uuid | NOT NULL | UQ (user_id, idempotency_key) | Header `Idempotency-Key` do web sinh mỗi lần mở trang thanh toán |
| status | text | `'pending_payment'` | CHECK enum | pending_payment · paid · fulfilled · cancelled · refunded |
| customer_email | citext | NOT NULL | | Snapshot từ tài khoản lúc đặt |
| customer_name | text | NOT NULL | CHECK 1–100 | Snapshot |
| vat_info | jsonb | NULL | CHECK là object | `{companyName, taxCode, address, email}` — xuất hoá đơn |
| subtotal_vnd | bigint | NOT NULL | CHECK ≥ 0 | Σ line_total |
| discount_vnd | bigint | 0 | CHECK 0 ≤ … ≤ subtotal | Phase 3 luôn 0 (coupon ở Phase 4) |
| total_vnd | bigint | NOT NULL | CHECK = subtotal − discount, > 0 | Số tiền phải chuyển — đúng số này mới khớp |
| expires_at | timestamptz | NOT NULL | | created_at + 30 phút |
| paid_at · fulfilled_at · cancelled_at · refunded_at | timestamptz | NULL | CHECK theo status | |
| cancel_reason | text | NULL | CHECK enum | expired · customer · admin |
| note | text | NULL | CHECK ≤ 1000 | |
| xmin | xid | hệ thống | concurrency token | |
| created_at · updated_at | timestamptz | NOT NULL | | |

Index: `ux_orders_code`; `ux_orders_user_idempotency (user_id, idempotency_key)`; `ix_orders_user_created (user_id, created_at DESC)`
— “Đơn của tôi” và đếm đơn đang chờ; `ix_orders_pending_expires (expires_at) WHERE status = 'pending_payment'` — job hết hạn;
`ix_orders_status_created (status, created_at DESC, id DESC)` — danh sách admin (keyset).

### 3.2 `sales.order_items`
| Cột | Kiểu | Ràng buộc | Ghi chú |
|---|---|---|---|
| id | uuid | PK | |
| order_id | uuid | FK → orders | |
| variant_id | uuid | FK → catalog.product_variants | Gói xoá mềm vẫn trỏ được |
| product_name_snapshot · variant_name_snapshot · sku_snapshot | text | NOT NULL | Admin đổi tên/giá sau đó không đổi chứng từ |
| delivery_type | text | CHECK license_key · account · manual | Snapshot — quyết định luồng giao |
| unit_price_vnd | bigint | CHECK ≥ 0 | Giá lấy từ DB lúc đặt, **không** nhận từ client |
| quantity | integer | CHECK 1–20 | Giới hạn 20 (v1.0: 100) — giảm rủi ro giữ kho hàng loạt |
| line_total_vnd | bigint | CHECK = unit × qty | |
| fulfilled_at | timestamptz NULL | | |

Index: `ix_order_items_order_id`, `ix_order_items_variant_id`. UNIQUE `(order_id, variant_id)` — một gói một dòng, gộp số lượng.

### 3.3 `sales.order_status_history` (chỉ thêm)
`id bigint IDENTITY PK` · `order_id FK` · `from_status NULL` · `to_status` · `actor_type` (customer · admin · system · webhook) ·
`actor_user_id NULL` (không FK — lịch sử sống lâu hơn tài khoản) · `reason NULL` (expired · late_payment · …) · `created_at`.
CHECK `from_status IS DISTINCT FROM to_status`. Index `(order_id, id)`. Quyền app: **chỉ INSERT, SELECT**.

### 3.4 `payments.webhook_events` (hộp thư đến)
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid PK | |
| provider | text CHECK | `sepay` (Phase 3) · `payos` · `manual` |
| event_key | text | `id` giao dịch SePay; UQ (provider, event_key) — trùng ⇒ trả 200 ngay |
| signature_valid | boolean | false ⇒ chỉ lưu làm bằng chứng, không xử lý, trả 401 |
| remote_ip | inet | IP thật sau Nginx — đối chiếu danh sách IP SePay |
| payload | jsonb | Body gốc (đã parse sau khi kiểm chữ ký trên raw body) |
| headers | jsonb NULL | Chỉ giữ header không nhạy cảm (bỏ chữ ký, Authorization) |
| received_at · processed_at NULL | timestamptz | |
| attempts | integer ≥ 0 | Job xử lý lại tối đa 10 lần |
| last_error | text NULL | |

Index `ux_webhook_events_provider_key`; `ix_webhook_events_unprocessed (received_at) WHERE processed_at IS NULL AND signature_valid`.
Quyền app: INSERT, SELECT, UPDATE (`processed_at`, `attempts`, `last_error`), DELETE (job dọn > 180 ngày).
**Chống làm đầy DB:** sự kiện sai chữ ký chỉ lưu tối đa 1.000 dòng/ngày (đếm trước khi ghi), quá thì chỉ log.

### 3.5 `payments.payments` (chỉ thêm, trừ cột đối soát)
| Cột | Kiểu | Ràng buộc | Ghi chú |
|---|---|---|---|
| id | uuid | PK | |
| order_id | uuid | **NULL** · FK → sales.orders | NULL = chưa khớp được đơn |
| provider | text | CHECK enum | |
| provider_txn_id | text | UQ (provider, …) | Chốt idempotency thứ hai |
| bank_reference | text NULL | | `referenceCode` của ngân hàng — tra soát với ngân hàng |
| transfer_content | text | NOT NULL | Nội dung chuyển khoản gốc |
| amount_vnd | bigint | CHECK > 0 | |
| match_status | text | CHECK enum | `matched` · `underpaid` · `overpaid` · `no_order_code` · `unknown_order` · `order_not_payable` · `late_no_stock` |
| webhook_event_id | uuid | FK → webhook_events · UQ | |
| paid_at | timestamptz | | Thời điểm ngân hàng ghi nhận |
| resolved_at · resolved_by · resolution_note | NULL | | Admin xử lý khoản lệch (Phase 4) |
| created_at | timestamptz | | |

`overpaid` vẫn cấp key (đơn chuyển `paid`), phần thừa chờ hoàn. Mọi `match_status ≠ matched` vào hàng chờ admin.
Index `ux_payments_provider_txn`; `ix_payments_order_id`; `ix_payments_unresolved (created_at) WHERE match_status <> 'matched' AND resolved_at IS NULL`.
Quyền app: INSERT, SELECT, UPDATE chỉ trên 3 cột `resolved_*` (GRANT UPDATE theo cột). Giữ lâu dài (chứng từ kế toán).

### 3.6 `inventory.import_batches`
`id` · `variant_id FK` · `source` (cli · admin) · `file_name` · `total_rows` · `inserted_rows` · `duplicate_rows` · `invalid_rows` ·
`imported_by uuid NULL FK → identity.users` (NULL khi CLI) · `note NULL` (số hoá đơn nhà phân phối) · `created_at`.
CHECK `inserted + duplicate + invalid = total`. Không lưu file gốc (chứa key rõ).

### 3.7 `inventory.license_keys`
| Cột | Kiểu | Ràng buộc | Ghi chú |
|---|---|---|---|
| id | uuid | PK | |
| variant_id | uuid | FK → catalog.product_variants | Cũng là AAD khi mã hoá (ADR-014) |
| batch_id | uuid | FK → import_batches | |
| key_encrypted | bytea | NOT NULL | nonce(12) ‖ ciphertext ‖ tag(16) |
| key_version | smallint | CHECK > 0 | |
| key_hash | bytea | UQ · CHECK 32 byte | HMAC-SHA256(key chuẩn hoá) |
| key_hint | text | CHECK 1–8 ký tự | 4 ký tự cuối |
| status | text | CHECK enum | `available` · `reserved` · `delivered` · `revoked` |
| order_item_id | uuid NULL | FK → sales.order_items | Gán khi giữ |
| reserved_at · delivered_at | timestamptz NULL | | |
| revoked_reason | text NULL | | |
| xmin | xid | concurrency token | |
| created_at | timestamptz | | Thứ tự FIFO |

Ràng buộc trạng thái viết theo từng trạng thái (key `revoked` sau khi giao vẫn giữ `order_item_id`) — xem DDL ở tài liệu
thiết kế PR-9 mục A4.
Index: `ux_license_keys_key_hash`; `ix_license_keys_available (variant_id, created_at) WHERE status = 'available'` — cấp FIFO và
đếm tồn kho; `ix_license_keys_order_item (order_item_id) WHERE order_item_id IS NOT NULL`.

Giữ key khi tạo đơn:
```sql
SELECT id FROM inventory.license_keys
 WHERE variant_id = $1 AND status = 'available'
 ORDER BY created_at LIMIT $2
 FOR UPDATE SKIP LOCKED;          -- 2 khách cùng lúc không lấy trùng key, không chờ nhau
-- ít hơn $2 dòng ⇒ ROLLBACK, trả 409 out_of_stock
UPDATE inventory.license_keys SET status = 'reserved', order_item_id = $3, reserved_at = now() WHERE id = ANY($4);
```

## 4. Chống lạm dụng giữ kho

Giữ key khi tạo đơn mở ra khả năng tạo nhiều đơn rồi bỏ để “khoá” kho. Bốn lớp chặn:
1. Bắt buộc đăng nhập (mỗi tài khoản cần email thật — xác minh email ở Phase 4).
2. Tối đa **3 đơn `pending_payment`** mỗi tài khoản (đếm bằng `ix_orders_user_created`).
3. Rate limit `POST /api/orders`: 10/giờ/IP + 20/ngày/tài khoản (cơ chế ADR-012).
4. `quantity ≤ 20` mỗi dòng; đơn hết hạn sau 30 phút ⇒ key tự trả về kho.

## 5. Quyền DB (bổ sung `roles.sql` / migration)

Mặc định `dxtechs_app` có SELECT, INSERT, UPDATE, DELETE trên bảng mới (ADR-009, default privileges). Thu hồi bớt ở **cả migration
lẫn `roles.sql`** — `roles.sql` chạy lại mỗi lần deploy và cấp lại quyền (chi tiết: thiết kế PR-9 mục A5):

| Bảng | Quyền app |
|---|---|
| orders, order_items, license_keys, import_batches | SELECT, INSERT, UPDATE (không DELETE) |
| order_status_history | SELECT, INSERT |
| payments | SELECT, INSERT, UPDATE(resolved_at, resolved_by, resolution_note) |
| webhook_events | SELECT, INSERT, UPDATE, DELETE (dọn 180 ngày) |

Test `test-db-roles.sh` thêm: app không DELETE được orders/payments, không UPDATE được `payments.amount_vnd`.

## 6. API Phase 3

| Method · Path | Xác thực | Mô tả | PR |
|---|---|---|---|
| `POST /api/cart/quote` | — | Giỏ hàng (lưu ở trình duyệt) → giá, tồn kho, tổng tiền tính ở server | PR-10 |
| `POST /api/orders` | Bearer + `Idempotency-Key` | Tạo đơn, giữ key; trả mã đơn, số tiền, hướng dẫn chuyển khoản + chuỗi VietQR, `expiresAt` | PR-11 |
| `GET /api/orders/{code}` | Bearer (chủ đơn / admin) | Trạng thái (web hỏi mỗi 3 giây khi chờ trả tiền); key rõ khi `fulfilled` | PR-11 |
| `GET /api/me/orders` | Bearer | “Đơn của tôi” (keyset) | PR-11 |
| `POST /api/orders/{code}/cancel` | Bearer | Khách huỷ đơn đang chờ | PR-11 |
| `POST /api/payments/webhooks/sepay` | Chữ ký HMAC | ADR-013 mục 3 | PR-12 |
| `GET /api/admin/payments?status=unresolved` | Admin | Hàng chờ đối soát (đọc) | PR-12 |

**VietQR tự sinh, không gọi dịch vụ ngoài:** server dựng chuỗi chuẩn EMVCo/NAPAS (mã BIN ngân hàng, số tài khoản, số tiền, nội dung
= mã đơn, CRC16) — chỉ là ghép chuỗi, không cần gói NuGet; web vẽ QR bằng thư viện JS. Không gửi thông tin đơn cho bên thứ ba
(khác cách dùng ảnh QR từ `img.vietqr.io` / `qr.sepay.vn`).

Job nền: `OrderExpiry` mỗi phút (huỷ đơn quá hạn theo lô 100, trả key); `WebhookRetry` mỗi phút; `WebhookCleanup` hằng ngày.

## 7. Lộ trình PR Phase 3

| PR | Nội dung | Điều kiện |
|---|---|---|
| PR-9 | Migration `Phase3_Sales_Payments_Inventory` (7 bảng, quyền, SQL review) + entity + test ràng buộc | Duyệt tài liệu này |
| PR-10 | `POST /api/cart/quote` + tồn kho trên trang sản phẩm | PR-9 |
| PR-11 | `ILicenseKeyProtector` (ADR-014), CLI `--import-keys`, tạo/xem/huỷ đơn, job hết hạn | PR-9; khoá `LICENSE_*` trong `.env` |
| PR-12 | Webhook SePay, cấp key, tiền đến muộn, hàng chờ đối soát | PR-11; tài khoản SePay + secret |
| PR-C4 | Web: giỏ hàng thật, trang thanh toán QR + đếm ngược, “Đơn của tôi” | PR-11 |

## 8. Câu hỏi còn mở (không chặn PR-9)
1. Email gửi key/xác nhận đơn: chọn nhà cung cấp gửi mail (Phase 3 có thể mở bán chỉ với “Đơn của tôi”).
2. Gói `account` / `manual`: Phase 3 đơn dừng ở `paid`, admin giao bằng tay và bấm hoàn tất (màn hình admin Phase 4; tạm thời CLI).
3. Xuất hoá đơn VAT điện tử: ngoài phạm vi Phase 3 — chỉ lưu `vat_info`.
