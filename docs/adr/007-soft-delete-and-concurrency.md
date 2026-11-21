# ADR-007: Soft delete chỉ cho catalog; concurrency bằng xmin

- **Trạng thái:** Đã áp dụng — PR-3 (`ISoftDeletable` + query filter + unique index `WHERE deleted_at IS NULL`; `xmin` trên `product_variants`)
- **Ngày:** 2026-11-18

## Bối cảnh
Đơn hàng cũ phải tham chiếu được sản phẩm đã ẩn; hai admin có thể sửa cùng lúc.

## Quyết định
`deleted_at` + global query filter cho `categories`, `products`, `product_variants`. Đơn hàng, thanh toán không soft delete (chỉ đổi trạng thái). `xmin` làm concurrency token cho `orders`, `product_variants` → 409 khi xung đột.

## Phương án đã cân nhắc và loại
Soft delete toàn bộ; không kiểm soát ghi đè.

## Hệ quả
Unique index của bảng soft delete phải có `WHERE deleted_at IS NULL`.

## Ghi chú triển khai (PR-3, 21/11/2026)
- `ProductVariant.Version` (uint) map tường minh vào cột hệ thống `xmin` kiểu `xid` — phải đặt `HasColumnName("xmin")`,
  nếu không convention snake_case đổi thành `version` (cột không tồn tại).
- Cột có DEFAULT trong DB (`is_active`, `seats`) dùng `HasDefaultValue(x).HasSentinel(x)`: nếu để sentinel mặc định
  của CLR, EF bỏ `is_active = false` khỏi INSERT và DB ghi `true` (lỗi đã bắt được bằng integration test).
