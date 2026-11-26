# Spike: cổng thanh toán cho Phase 3 (25/11/2026)

**Mục tiêu:** chọn cách nhận tiền cho đơn hàng license, đủ thông tin để viết ADR-013 và thiết kế bảng `payments`.
**Phạm vi:** đọc tài liệu chính thức. Chưa thử sandbox — cần tài khoản doanh nghiệp/hộ kinh doanh do công ty đăng ký
(mục 5). Mọi điểm “chưa xác nhận” phải kiểm tra lại khi có tài khoản thử.

## 1. Bối cảnh
- Khách mua phần mềm tại Việt Nam chủ yếu **chuyển khoản/quét VietQR** bằng app ngân hàng; thẻ quốc tế là thiểu số.
- Đơn giá vừa (vài trăm nghìn đến vài triệu ₫), số đơn năm đầu ước 500–5.000 ⇒ **phí theo % mỗi giao dịch** (cổng thẻ/ví)
  chiếm tỉ trọng đáng kể; phí cố định/tháng thấp có lợi hơn.
- Thiết kế DB v1.0 đã có: `payments` UNIQUE(provider, provider_txn_id), `webhook_events` (inbox), `matched` khi lệch tiền.

## 2. So sánh

| | **SePay** (đối soát tài khoản ngân hàng) | **payOS** (Casso — link thanh toán VietQR) | **VNPAY** (cổng thẻ/ATM/QR) | **MoMo** (ví) |
|---|---|---|---|---|
| Cách khách trả | Chuyển khoản / quét VietQR vào **tài khoản ngân hàng của công ty** | Quét VietQR trên trang thanh toán payOS, tiền vào **tài khoản của công ty** | Trang VNPAY: QR, thẻ ATM nội địa, thẻ quốc tế | App MoMo |
| Nhận diện đơn | Mã thanh toán trong **nội dung chuyển khoản** (trường `code`) | `orderCode` (số nguyên) gắn với link thanh toán | `vnp_TxnRef` | `orderId` |
| Xác thực webhook | **HMAC-SHA256** trên `"{timestamp}.{raw_body}"`, header `X-SePay-Signature: sha256=<hex>` + `X-SePay-Timestamp`; khuyến nghị từ chối lệch quá 5 phút; có API Key / OAuth 2.0 (không khuyến nghị) | HMAC-SHA256 bằng **checksum key** trên các trường của `data` **sắp xếp theo alphabet** `key=value&…` (không ký raw body, không có timestamp) | HMAC-SHA512 (`vnp_SecureHash`) trên tham số sắp xếp | HMAC-SHA256 (`signature`) |
| Gửi lại khi lỗi | Tối đa **7 lần**, cách 1-1-2-3-5-8-13 phút (~33 phút) khi không kết nối được/timeout (hoặc khi bật tuỳ chọn và nhận mã ≠ 2xx) | Chưa xác nhận | Có (số lần chưa xác nhận) | Có (chưa xác nhận) |
| Phản hồi thành công | HTTP 200/201 + `{"success": true}` | HTTP 2xx | JSON `RspCode` (`00` thành công; `02` đã xác nhận; `04` sai số tiền; `97` sai chữ ký) | HTTP 204 |
| Khoá chống trùng | `id` (giao dịch SePay); có `referenceCode` của ngân hàng | `reference` / `paymentLinkId` | `vnp_TransactionNo` | `transId` |
| IP nguồn | **Công bố** 7 IPv4 + 2 IPv6 | Chưa xác nhận | Có danh sách (theo hợp đồng) | Có |
| Đối soát | API giao dịch | API lấy thông tin link | API truy vấn | API truy vấn |
| Chi phí | Gói Free có giới hạn giao dịch/tháng; gói trả phí theo tháng (bảng giá sepay.vn) | Có gói **miễn phí** (600 giao dịch; gói không giới hạn qua ngân hàng đối tác từ 23/01/2026 cho cá nhân/HKD; DN có ưu đãi riêng) | % mỗi giao dịch + phí theo hợp đồng | % mỗi giao dịch |
| Giấy tờ | Đăng ký tài khoản; liên kết tài khoản ngân hàng | Đăng ký; tài khoản ngân hàng hỗ trợ | **Hợp đồng merchant**, giấy phép kinh doanh, thẩm định website | Hợp đồng doanh nghiệp |
| Ràng buộc đáng chú ý | Khách có thể gõ **sai nội dung/sai số tiền** khi chuyển tay ⇒ cần xử lý lệch | `description` chỉ **9 ký tự** với tài khoản không liên kết payOS | Thời gian duyệt hợp đồng; phí % | Phí %; chỉ người dùng MoMo |

Nguồn: developer.sepay.vn (Xác thực webhook), docs.sepay.vn (Tích hợp Webhooks), sepay.vn/bang-gia; payos.vn/docs
(API, Kiểm tra dữ liệu với signature), payos.vn (bảng giá, tin 28/01/2026); tài liệu tích hợp VNPAY (sandbox.vnpayment.vn —
không tải được trong spike, đối chiếu qua bài hướng dẫn cộng đồng), developers.momo.vn (tóm tắt).

## 3. Phát hiện ảnh hưởng thiết kế
1. **Mã đơn không được có ký tự đặc biệt.** Nhiều app ngân hàng bỏ `-` trong nội dung chuyển khoản; payOS giới hạn 9 ký tự.
   ⇒ Đổi `orders.code` từ `DX-261123-7K3Q` (thiết kế DB v1.0) sang **`DX` + 8 ký tự Crockford base32** (`DX7K3QH9M2`, 10 ký tự,
   không có I/L/O/U). Hiển thị có thể tách nhóm `DX 7K3Q H9M2`, nhưng giá trị lưu và chuyển khoản không có dấu.
   Với payOS dùng `orderCode` số (khoá riêng) nên giới hạn 9 ký tự không ảnh hưởng.
2. **Chuyển khoản tay có thể lệch** (sai số tiền, thiếu mã, chuyển 2 lần). Bảng `payments.matched` + hàng chờ admin đã có
   trong thiết kế — giữ nguyên, thêm trạng thái “tiền đến sau khi đơn hết hạn”.
3. **Webhook phải dùng raw body** để kiểm chữ ký (SePay) — ASP.NET Core đọc `Request.Body` trước khi model binding
   (bật `EnableBuffering`), không dùng body đã parse.
4. **Chống replay**: SePay có timestamp (±5 phút). payOS không có ⇒ dựa vào idempotency (inbox UNIQUE + payments UNIQUE),
   đủ an toàn vì xử lý trùng không tạo thêm tiền/key.
5. Hai dịch vụ đối soát (SePay, payOS) đều **không giữ tiền** — tiền vào thẳng tài khoản công ty ⇒ không có rủi ro tiền kẹt ở
   trung gian; hoàn tiền là chuyển khoản tay của công ty.

## 4. Đề xuất (chi tiết ở ADR-013)
- Phase 3: **chuyển khoản VietQR qua dịch vụ đối soát**, thiết kế độc lập nhà cung cấp (`IPaymentProvider`).
- Nhà cung cấp đầu tiên: **SePay** — xác thực webhook mạnh nhất (HMAC raw body + timestamp + danh sách IP), lịch gửi lại
  được công bố, hỗ trợ nhiều ngân hàng, có API giao dịch để đối soát. **payOS** là phương án thay thế ngang hàng
  (chi phí thấp hơn, có link thanh toán theo đơn) — chọn khi tài khoản ngân hàng của công ty thuộc diện miễn phí.
- Thẻ/ví (VNPAY, MoMo): **sau mở bán**, khi có nhu cầu thật — cần hợp đồng merchant và chịu phí %.

## 5. Việc cần công ty / Backend Lead
| # | Việc | Ai |
|---|---|---|
| 1 | Chọn tài khoản ngân hàng nhận tiền (tài khoản doanh nghiệp/HKD, đúng tên pháp nhân bán hàng) | Công ty |
| 2 | Đăng ký SePay (hoặc payOS), liên kết tài khoản, tạo webhook **HMAC-SHA256** trỏ tới `https://api.dxtechs.com/api/payments/webhooks/sepay`, lưu Secret Key vào `.env` | Backend Lead |
| 3 | Thử: chuyển 2.000 ₫ với nội dung chứa mã thử ⇒ xem webhook đến (log), số tiền, `id`, `referenceCode` | Backend Lead + Claude |
| 4 | Xác nhận các điểm “chưa xác nhận” ở bảng trên với tài khoản thật | Claude |
