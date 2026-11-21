# ADR-006: snake_case, UUID v7, tiền bigint VND, enum text, timestamptz UTC

- **Trạng thái:** Đã áp dụng — PR-1 (snake_case, UuidV7, IAuditable + TimeProvider); PR-3 (`price_vnd bigint`, enum text + CHECK qua `SnakeCaseEnumConverter`)
- **Ngày:** 2026-11-18

## Bối cảnh
Cần quy ước dữ liệu thống nhất trước bảng đầu tiên.

## Quyết định
Tên DB snake_case qua `SnakeCaseNamingConvention` tự viết (tên đặt tường minh được giữ nguyên; luôn `ToTable("tên_số_nhiều", schema)`). Khoá chính UUID v7 sinh ở app (`Shared/Ids/UuidV7`, vì .NET 8 chưa có `Guid.CreateVersion7`). Tiền `bigint` hậu tố `_vnd`. Enum lưu `text` + `CHECK`. Thời gian `timestamptz`, UTC, lấy từ `TimeProvider`; `created_at/updated_at` do `AuditableInterceptor` điền.

## Phương án đã cân nhắc và loại
PascalCase mặc định, UUID v4/bigint identity, `numeric`, PostgreSQL ENUM, `timestamp` không múi giờ.

## Hệ quả
Báo cáo theo ngày Việt Nam phải dùng `AT TIME ZONE 'Asia/Ho_Chi_Minh'` và so sánh theo khoảng UTC (xem tài liệu thiết kế mục 10).
