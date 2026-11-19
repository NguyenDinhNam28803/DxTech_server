# ADR-009: Tách role database: owner (migrate) và app (API)

- **Trạng thái:** Đã chấp nhận — áp dụng ở PR-2 (cần thao tác trên VPS)
- **Ngày:** 2026-11-18

## Bối cảnh
API đang kết nối bằng `POSTGRES_USER` — superuser của container.

## Quyết định
`dxtechs_owner` sở hữu schema, chỉ container `migrate` dùng. `dxtechs_app` chỉ có SELECT/INSERT/UPDATE/DELETE, container `api` dùng. `ALTER DEFAULT PRIVILEGES` cấp quyền tự động cho bảng mới.

## Phương án đã cân nhắc và loại
Giữ một superuser cho mọi thứ.

## Hệ quả
Script tạo role phải chạy tay một lần trên VPS (init script của image chỉ chạy khi volume rỗng). Phải có backup trước khi làm.
