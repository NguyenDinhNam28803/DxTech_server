# ADR-003: Handler gọi trực tiếp, DbContext trực tiếp, map tay

- **Trạng thái:** Đã chấp nhận — áp dụng từ PR-4
- **Ngày:** 2026-11-18

## Bối cảnh
Ràng buộc không thêm thư viện bên thứ ba; cần code dễ lần theo.

## Quyết định
Controller gọi thẳng class handler đăng ký DI theo module (không MediatR). Handler dùng `AppDbContext` trực tiếp (không Repository/UoW), truy vấn đọc dùng `AsNoTracking` + `Select` sang DTO viết tay (không AutoMapper). SQL đặc biệt (`FOR UPDATE SKIP LOCKED`) dùng `FromSql`.

## Phương án đã cân nhắc và loại
MediatR, Repository + Unit of Work, AutoMapper/Mapster, Dapper.

## Hệ quả
Không mock được database ⇒ test với PostgreSQL thật (ADR-005). Viết mapping nhiều hơn một chút nhưng SQL chỉ lấy đúng cột cần.
