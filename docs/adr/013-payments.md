# ADR-013: Nhận thanh toán — chuyển khoản VietQR qua dịch vụ đối soát, webhook idempotent

- **Trạng thái:** Đã duyệt (26/11/2026) — nhà cung cấp cụ thể (SePay/payOS) chốt khi công ty chọn tài khoản ngân hàng;
  thiết kế bảng: `docs/database/phase3-schema.md`
- **Ngày:** 2026-11-25
- **Liên quan:** ADR-002 (một DB, transaction chung), ADR-006 (tiền bigint VND), ADR-012 (Origin, rate limit);
  *Thiết kế toàn bộ Database v1.0* mục 7–8, 11; spike `docs/spikes/2026-11-25-payment-gateways.md`

## Bối cảnh
Phase 3 cần nhận tiền thật cho đơn hàng license và cấp key ngay khi tiền về. Yêu cầu: (1) không cấp key khi chưa chắc
đã nhận đủ tiền; (2) một khoản tiền không bao giờ được ghi hai lần dù nhà cung cấp gửi webhook lặp lại; (3) không mất
thông báo thanh toán khi API lỗi giữa chừng; (4) chi phí thấp với quy mô năm đầu; (5) không phụ thuộc chặt vào một nhà
cung cấp.

## Quyết định

### 1. Phương thức & nhà cung cấp
- Phase 3: **chuyển khoản VietQR** vào tài khoản ngân hàng của công ty, xác nhận bằng **webhook của dịch vụ đối soát**.
- Nhà cung cấp đầu tiên: **SePay** (webhook HMAC-SHA256 trên `timestamp.raw_body`, chống replay ±5 phút, danh sách IP,
  gửi lại 7 lần được công bố). **payOS** là phương án thay thế; chọn theo tài khoản ngân hàng và chi phí của công ty.
- Code gói sau interface `IPaymentProvider` (tạo hướng dẫn thanh toán / QR; kiểm chữ ký; đọc sự kiện thành dạng chung
  `ProviderPayment { provider, txnId, amountVnd, reference, paidAt, raw }`). Đổi nhà cung cấp = thêm một adapter.
- Thẻ/ví (VNPAY, MoMo): sau mở bán, khi có nhu cầu (hợp đồng merchant, phí %).

### 2. Mã đơn & nội dung chuyển khoản
- `orders.code` = **`DX` + 8 ký tự Crockford base32** (bỏ I, L, O, U), ví dụ `DX7K3QH9M2`. **Không có dấu `-`** vì app ngân hàng
  thường bỏ ký tự đặc biệt. Hiển thị cho người đọc có thể tách nhóm, giá trị lưu/chuyển khoản luôn liền.
  *Thay cho* `DX-261123-7K3Q` trong thiết kế DB v1.0 (ngày đã có trong `created_at`).
- QR/hướng dẫn chuyển khoản điền sẵn **số tiền đúng** và **nội dung = mã đơn**.
- Nhận diện: tìm mã khớp `DX[0-9A-HJKMNP-TV-Z]{8}` trong nội dung (không phân biệt hoa thường, bỏ khoảng trắng).
  Không tìm thấy mã ⇒ `payments.order_id = NULL`, `matched = false` ⇒ hàng chờ admin.

### 3. Luồng webhook (một endpoint mỗi nhà cung cấp)
`POST /api/payments/webhooks/{provider}` — `[AllowAnonymous]`, không qua CORS (server-to-server), không rate limit theo IP
khách nhưng có giới hạn tổng.

1. Đọc **raw body** (bật `EnableBuffering`, đọc trước model binding). Kiểm chữ ký bằng so sánh thời gian hằng
   (`CryptographicOperations.FixedTimeEquals`); kiểm timestamp ±5 phút (SePay). Kiểm IP nguồn nếu nhà cung cấp công bố
   (lấy IP thật sau Nginx như ADR-012).
2. **Ghi inbox trước** `payments.webhook_events` (UNIQUE `(provider, event_key)`, `event_key` = id giao dịch của nhà cung cấp):
   - Trùng ⇒ trả thành công ngay (đã nhận).
   - Sai chữ ký ⇒ vẫn lưu với `signature_valid = false` (bằng chứng/điều tra) và trả 401; **không xử lý**.
3. Xử lý trong **một transaction** (thiết kế DB mục 11.3): `INSERT payments` (UNIQUE `(provider, provider_txn_id)`) →
   đối chiếu đơn → nếu khớp: `orders` `pending_payment → paid` (UPDATE có điều kiện trạng thái + `xmin`) → cấp key
   `FOR UPDATE SKIP LOCKED` → `fulfilled` → lịch sử trạng thái → `webhook_events.processed_at`.
4. Trả thành công cho nhà cung cấp **sau khi COMMIT** (SePay: 200 `{"success": true}`). Lỗi trong bước 3 ⇒ ROLLBACK,
   inbox còn `processed_at = NULL`, trả 5xx ⇒ nhà cung cấp gửi lại; job nền mỗi phút xử lý lại inbox chưa xử lý
   (tối đa 10 lần, sau đó cảnh báo admin).
5. Gửi email key **sau COMMIT**, không nằm trong transaction (email lỗi không làm mất key; khách xem trong “Đơn của tôi”).

### 4. Quy tắc đối chiếu tiền
| Tình huống | Xử lý |
|---|---|
| Đúng mã, đúng số tiền, đơn `pending_payment` | `matched = true` ⇒ `paid` ⇒ cấp key |
| Đúng mã, **thiếu** tiền | `matched = false`, đơn giữ nguyên; admin liên hệ khách (chuyển thêm hoặc hoàn tiền) |
| Đúng mã, **thừa** tiền | `matched = true` cho đơn; ghi chú số thừa để admin hoàn phần thừa (Phase 4 `refunds`) |
| Đơn đã `cancelled` vì hết hạn, tiền đến **trong 24 giờ**, đúng số tiền, còn key | Mở lại: `cancelled → paid` (chuyển đổi duy nhất được phép đi ngược, ghi lịch sử `actor_type = webhook`, lý do `late_payment`) |
| Đơn đã `paid`/`fulfilled` và lại có khoản tiền mới cùng mã | Khoản thứ hai `matched = false` ⇒ admin hoàn tiền |
| Không có mã / mã lạ | `order_id = NULL`, `matched = false` ⇒ hàng chờ admin |

### 5. Hết hạn đơn
Đơn `pending_payment` hết hạn sau **30 phút** (job mỗi phút). Trang thanh toán hiển thị đếm ngược và trạng thái theo
thời gian thực bằng cách hỏi `GET /api/orders/{code}` mỗi 3 giây (không cần WebSocket ở quy mô này).

### 6. Bí mật & cấu hình
`PAYMENTS_SEPAY_WEBHOOK_SECRET` (và `PAYMENTS_SEPAY_API_KEY` cho API đối soát) trong `.env`, compose dùng `${…:?}`
như `AUTH_SIGNING_KEY`. Thông tin tài khoản nhận tiền (ngân hàng, số tài khoản, tên) là cấu hình công khai trong `Payments:*`.

## Phương án đã cân nhắc và loại
| Phương án | Vì sao không chọn (bây giờ) |
|---|---|
| VNPAY/MoMo ngay Phase 3 | Hợp đồng merchant + thẩm định mất thời gian; phí % trên mỗi đơn; khách mua license chủ yếu chuyển khoản. Thêm sau qua `IPaymentProvider` |
| Tự đọc biến động số dư (email/SMS ngân hàng) | Không có chữ ký, dễ giả mạo, dễ vỡ khi ngân hàng đổi định dạng |
| Xác nhận thanh toán bằng tay (admin bấm “đã nhận tiền”) | Chậm, sai sót, không cấp key ngay; giữ làm **đường dự phòng** trong admin (Phase 4) |
| Xử lý webhook bất đồng bộ hoàn toàn (chỉ ghi inbox, job xử lý sau) | An toàn nhưng chậm vài giây–phút mới có key; chọn xử lý đồng bộ + job dự phòng |
| Tin `Return URL` (trình duyệt quay về) để xác nhận đơn | Trình duyệt có thể giả; chỉ webhook có chữ ký mới làm thay đổi trạng thái tiền |

## Hệ quả
- Sửa thiết kế DB v1.0: định dạng `orders.code`; `payments.order_id` cho phép NULL (tiền chưa khớp đơn); cờ `matched`
  chi tiết hoá thành `match_status` (xem `phase3-schema.md` mục 3.5);
  thêm chuyển đổi `cancelled → paid` (late payment) vào máy trạng thái.
- Hoàn tiền là thao tác ngoài hệ thống (chuyển khoản của công ty), hệ thống chỉ ghi nhận (`refunds`, Phase 4).
- Kiểm thử bắt buộc (PR-11): chữ ký sai/thiếu/timestamp cũ ⇒ 401 + inbox `signature_valid=false`; webhook ×10 ⇒ 1 payment;
  lệch tiền ⇒ không cấp key; tiền đến sau hết hạn; 50 đơn đồng thời không trùng key; lỗi giữa transaction ⇒ không có
  payment nửa vời, job xử lý lại thành công.

## Ghi chú triển khai (PR-11, 27/11/2026)
- Hướng dẫn chuyển khoản: `Payments.Contracts.IPaymentInstructions`; chuỗi VietQR tự dựng (`Modules/Payments/Features/VietQr.cs`:
  EMVCo, NAPAS `A000000727`, dịch vụ `QRIBFTTA`, CRC-16/CCITT-FALSE). **Cần quét thử bằng app vài ngân hàng trước khi mở bán.**
- Tài khoản nhận tiền qua `Payments:BankTransfer:*` (biến `PAYMENTS_*`); chưa cấu hình ⇒ không nhận đặt hàng (422), phần còn lại chạy bình thường.
- Đặt hàng: advisory lock theo người dùng trong transaction ⇒ giới hạn 3 đơn chờ và idempotency không bị vượt do đua; báo giá lại
  bằng cùng lõi PR-10; thiếu key khi giữ ⇒ ROLLBACK cả đơn. Webhook + tiền đến muộn: PR-12.

## Ghi chú triển khai (PR-12, 27/11/2026)
- `Payments/Features/Sepay`: chữ ký `sha256=<hex>` = HMAC-SHA256(secret, "{timestamp}.{raw body}"), so sánh thời gian hằng; lệch giờ > 300 giây ⇒ 401;
  IP ngoài danh sách SePay (appsettings, tắt được bằng `Payments:Sepay:CheckIps=false`) ⇒ 401. Body đọc thẳng từ `Request.Body` (≤ 64 KB) —
  không qua model binding nên chữ ký tính trên đúng byte nhận được.
- Ranh giới module: Payments chỉ ghi nhận tiền; luật áp tiền vào đơn nằm ở Sales (`Sales.Contracts.IOrderPayments`), chạy trong cùng transaction,
  khoá dòng đơn `FOR UPDATE` — webhook, job hết hạn, khách huỷ không đè nhau.
- Thời điểm giao dịch SePay là giờ Việt Nam ⇒ đổi sang UTC trước khi lưu (phát hiện khi viết test: Npgsql từ chối offset +07).
- Hai lớp chống trùng: UNIQUE `webhook_events (provider, event_key)` và UNIQUE `payments (provider, provider_txn_id)`. Thử phá lớp thứ nhất
  (mỗi lần gửi lại thành sự kiện mới) ⇒ 10 lần gửi vẫn chỉ 1 payment nhờ lớp thứ hai.
- Job `WebhookMaintenance`: xử lý lại sự kiện treo mỗi phút (≤ 10 lần, 50 sự kiện/lượt), dọn sự kiện > 180 ngày không gắn payment.
- ⚠️ Trước khi mở bán: chuyển thật 2.000 ₫ để xác nhận tên trường payload SePay (`id`, `transferAmount`, `content`, `transactionDate`…) và định dạng chữ ký.
