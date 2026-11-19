# ADR-007: Soft delete chỉ cho catalog; concurrency bằng xmin

- **Trạng thái:** Đã chấp nhận — áp dụng ở PR-3
- **Ngày:** 2026-11-18

## Bối cảnh
Đơn hàng cũ phải tham chiếu được sản phẩm đã ẩn; hai admin có thể sửa cùng lúc.

## Quyết định
`deleted_at` + global query filter cho `categories`, `products`, `product_variants`. Đơn hàng, thanh toán không soft delete (chỉ đổi trạng thái). `xmin` làm concurrency token cho `orders`, `product_variants` → 409 khi xung đột.

## Phương án đã cân nhắc và loại
Soft delete toàn bộ; không kiểm soát ghi đè.

## Hệ quả
Unique index của bảng soft delete phải có `WHERE deleted_at IS NULL`.
