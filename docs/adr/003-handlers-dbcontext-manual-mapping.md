# ADR-003: Handler gọi trực tiếp, DbContext trực tiếp, map tay

- **Trạng thái:** Đã áp dụng — PR-4 (Catalog: `GetCategoryTree`, `ListProducts`, `GetProductBySlug`)
- **Ngày:** 2026-11-18

## Bối cảnh
Ràng buộc không thêm thư viện bên thứ ba; cần code dễ lần theo.

## Quyết định
Controller gọi thẳng class handler đăng ký DI theo module (không MediatR). Handler dùng `AppDbContext` trực tiếp (không Repository/UoW), truy vấn đọc dùng `AsNoTracking` + `Select` sang DTO viết tay (không AutoMapper). SQL đặc biệt (`FOR UPDATE SKIP LOCKED`) dùng `FromSql`.

## Phương án đã cân nhắc và loại
MediatR, Repository + Unit of Work, AutoMapper/Mapster, Dapper.

## Hệ quả
Không mock được database ⇒ test với PostgreSQL thật (ADR-005). Viết mapping nhiều hơn một chút nhưng SQL chỉ lấy đúng cột cần.

## Ghi chú triển khai (PR-4, 23/11/2026)
- Use case đầu tiên: `Modules/Catalog/Features/{Categories/GetCategoryTree, Products/ListProducts, Products/GetProductBySlug}`.
- Truy vấn đặc biệt dùng SQL tham số hoá qua `Database.SqlQuery<T>($"...")` (CTE đệ quy cây danh mục, breadcrumb) —
  interpolated string được EF đổi thành tham số, không ghép chuỗi.
- Hàm SQL dùng trong LINQ khai báo bằng `HasDbFunction` (`CatalogDbFunctions.Unaccent` → `catalog.f_unaccent`).
- Bài học hiệu năng: chiếu một object từ "phần tử đầu tiên của tập con" (`.Select(v => new {...}).First()`) khiến EF
  sinh `ROW_NUMBER()` trên toàn bảng con; dùng subquery vô hướng cho từng cột thay thế.
