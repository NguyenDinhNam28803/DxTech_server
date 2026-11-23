# ADR-010: Dữ liệu demo chạy theo lệnh

- **Trạng thái:** Đã áp dụng — PR-5 (`Modules/Catalog/Seeding`)
- **Ngày:** 2026-11-18

## Bối cảnh
Website hiện hiển thị sản phẩm mẫu từ `products.ts`; production sau này dùng dữ liệu thật do admin nhập.

## Quyết định
Dữ liệu tham chiếu ổn định đưa vào migration. Sản phẩm demo do seeder idempotent (upsert theo slug/sku) chạy khi có tham số `--seed-demo`; không tự chạy khi API khởi động.

## Phương án đã cân nhắc và loại
`HasData` (migration nhiễu), seed tự động lúc khởi động.

## Hệ quả
Chạy seed trên production là thao tác có chủ đích của người vận hành.

## Ghi chú triển khai (PR-5, 23/11/2026)
- Dữ liệu: `server/Modules/Catalog/Seeding/demo-catalog.json` (nhúng vào assembly), chuyển một lần từ
  `src/data/products.ts` của client: 16 danh mục con, 16 sản phẩm, 16 gói. Hãng, loại giấy phép, số tháng,
  số người dùng, cách giao hàng được suy ra/ánh xạ tường minh và review trong PR.
- **"Chỉ thêm cái còn thiếu" thay cho upsert:** slug đã tồn tại (kể cả bản đã xoá mềm) ⇒ bỏ qua, không ghi đè.
  Upsert sẽ âm thầm hoàn tác chỉnh sửa của admin và "hồi sinh" sản phẩm đã gỡ mỗi lần chạy lại seed.
- Một transaction cho cả lần seed; `--dry-run` chạy đủ (kể cả CHECK/UNIQUE của DB) rồi rollback.
- Chạy bằng role `dxtechs_app` (chỉ cần INSERT) qua chính image API:
  `docker compose ... run --rm --no-deps api --seed-demo [--dry-run]`; mã thoát 0/1.
