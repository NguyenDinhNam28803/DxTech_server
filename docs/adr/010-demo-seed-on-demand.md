# ADR-010: Dữ liệu demo chạy theo lệnh

- **Trạng thái:** Đã chấp nhận — áp dụng ở PR-5
- **Ngày:** 2026-11-18

## Bối cảnh
Website hiện hiển thị sản phẩm mẫu từ `products.ts`; production sau này dùng dữ liệu thật do admin nhập.

## Quyết định
Dữ liệu tham chiếu ổn định đưa vào migration. Sản phẩm demo do seeder idempotent (upsert theo slug/sku) chạy khi có tham số `--seed-demo`; không tự chạy khi API khởi động.

## Phương án đã cân nhắc và loại
`HasData` (migration nhiễu), seed tự động lúc khởi động.

## Hệ quả
Chạy seed trên production là thao tác có chủ đích của người vận hành.
