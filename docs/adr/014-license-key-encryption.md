# ADR-014: Mã hoá license key trong database

- **Trạng thái:** Đề xuất — chờ Backend Lead duyệt
- **Ngày:** 2026-11-25
- **Liên quan:** ADR-009 (role DB), ADR-013 (cấp key sau thanh toán); *Thiết kế toàn bộ Database v1.0* mục 9
  (`inventory.license_keys`), 13.3 (sao lưu khoá); backup mã hoá (`deploy/vps/backup/`)

## Bối cảnh
License key là tài sản bán được: lộ một bản dump database (backup thất lạc, SQL injection, máy dev) không được đồng nghĩa
với lộ toàn bộ kho key. Đồng thời phải (1) chống nhập trùng key mà không cần giải mã cả kho, (2) cấp key nhanh khi thanh
toán, (3) xoay khoá được, (4) chỉ dùng thư viện có sẵn của .NET.

## Quyết định
| Thành phần | Quyết định |
|---|---|
| Mã hoá | **AES-256-GCM** (`System.Security.Cryptography.AesGcm`, có sẵn .NET 8). Nonce 12 byte ngẫu nhiên mỗi key, tag 16 byte. Lưu `key_encrypted = nonce ‖ ciphertext ‖ tag` |
| Ràng buộc ngữ cảnh | **AAD** = `variant_id ‖ key_version` ⇒ không thể chép ciphertext của gói này sang gói khác mà vẫn giải mã được |
| Khoá mã hoá | 32 byte ngẫu nhiên, mỗi phiên bản một biến: `LICENSE_ENC_KEY_V1`, `LICENSE_ENC_KEY_V2`… (base64) trong `.env`; `License:CurrentVersion` chọn khoá để mã hoá mới |
| Chống trùng | `key_hash` = **HMAC-SHA256**(key chuẩn hoá, `LICENSE_HMAC_KEY`) + UNIQUE. Chuẩn hoá: bỏ khoảng trắng và `-`, chữ hoa. HMAC (không phải SHA thường) để kẻ có dump không thử ngược được key ngắn bằng từ điển |
| Hiển thị trong admin | `key_hint` = 4 ký tự cuối; giải mã đầy đủ chỉ khi giao cho khách hoặc admin bấm “xem” (ghi audit, Phase 4) |
| Ai giải mã | Chỉ module Inventory, qua `ILicenseKeyProtector`; key rõ không bao giờ ghi log, không nằm trong `audit_logs.diff` |
| Giao cho khách | Key rõ chỉ trả trong `GET /api/me/orders/{code}` của chính chủ đơn và trong email sau thanh toán |

### Xoay khoá
1. Thêm `LICENSE_ENC_KEY_V2` vào `.env`, đặt `License:CurrentVersion=2`, deploy ⇒ key mới nhập dùng V2, key cũ vẫn giải mã bằng V1.
2. Lệnh `--rewrap-license-keys` mã hoá lại các dòng `key_version = 1` sang V2 theo lô (trong transaction, `FOR UPDATE SKIP LOCKED`).
3. Khi không còn dòng V1: gỡ `LICENSE_ENC_KEY_V1`.
`LICENSE_HMAC_KEY` **không xoay định kỳ** (xoay = tính lại hash của cả kho từ key rõ); chỉ xoay khi nghi lộ, bằng lệnh tương tự.

### Sao lưu khoá (bắt buộc trước khi nhập key thật)
- Khoá nằm ngoài DB ⇒ backup DB **không** chứa khoá. Mất khoá = mọi key trong kho thành rác, kể cả khi backup DB còn.
- Bản sao khoá ở **ít nhất 2 nơi tách biệt và khác nơi giữ backup DB**: trình quản lý mật khẩu của Backend Lead + một người
  được công ty chỉ định (hoặc két). Không gửi qua chat/email.
- Diễn tập (Phase 5): khôi phục DB từ backup ngoài VPS + khoá từ nơi lưu ⇒ giải mã được một key mẫu.

## Phương án đã cân nhắc và loại
| Phương án | Vì sao không chọn |
|---|---|
| Lưu key rõ | Lộ dump = lộ toàn bộ tài sản |
| `pgcrypto` trong PostgreSQL | Khoá phải gửi vào câu SQL ⇒ có thể lọt vào log truy vấn / `pg_stat_statements`; DB vừa giữ dữ liệu vừa thấy khoá |
| SHA-256 thường cho chống trùng | Key ngắn/có mẫu ⇒ thử ngược được bằng từ điển khi có dump |
| AES-CBC + HMAC tự ghép | Dễ sai (padding oracle, thứ tự MAC); GCM đã có xác thực tích hợp |
| KMS/Vault bên ngoài | Thêm hạ tầng/nhà cung cấp; vượt nhu cầu hiện tại — xem lại khi có nhiều máy chủ |
| Mã hoá cả cột bằng một nonce cố định | Lặp nonce với GCM phá vỡ bảo mật — mỗi key phải có nonce riêng |

## Hệ quả
- Thêm cột so với thiết kế DB v1.0: không (đã có `key_encrypted`, `key_version`, `key_hash`, `key_hint`). AAD dùng `variant_id`
  ⇒ đổi gói của một key = mã hoá lại (thao tác admin hiếm, qua lệnh có kiểm soát).
- API không khởi động nếu thiếu khoá phiên bản hiện tại hoặc `LICENSE_HMAC_KEY`, hoặc khoá không đủ 32 byte (giống ADR-012).
- Kiểm thử bắt buộc: mã hoá → giải mã khớp; đổi 1 byte ciphertext/tag/AAD ⇒ lỗi; hai lần mã hoá cùng key ⇒ ciphertext khác
  nhau, hash giống nhau; nhập key trùng (khác định dạng `-`/hoa thường) ⇒ bị UNIQUE chặn; xoay V1→V2 giữ giải mã được;
  log không chứa key rõ.
