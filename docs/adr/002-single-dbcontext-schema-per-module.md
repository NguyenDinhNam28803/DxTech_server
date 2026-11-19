# ADR-002: Một AppDbContext, mỗi module một schema PostgreSQL

- **Trạng thái:** Đã chấp nhận — `AppDbContext` ở PR-1, schema đầu tiên (`catalog`) ở PR-3
- **Ngày:** 2026-11-18

## Bối cảnh
Cần transaction xuyên module và ranh giới dữ liệu rõ ràng.

## Quyết định
Một `AppDbContext` duy nhất (`Infrastructure/Persistence`), nạp cấu hình qua `ApplyConfigurationsFromAssembly`. Mỗi module đặt bảng vào schema riêng: `catalog`, `identity`, `sales`, `inventory`, `payments`, `audit`.

## Phương án đã cân nhắc và loại
DbContext riêng từng module: mất transaction chung. Tất cả trong `public`: không thấy ranh giới.

## Hệ quả
FK xuyên schema được phép (cùng database) nhưng code chỉ đi qua Contracts. Tên bảng dài hơn (`catalog.products`).
