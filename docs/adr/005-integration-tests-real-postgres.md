# ADR-005: Integration test với PostgreSQL thật

- **Trạng thái:** Đã áp dụng — PR-1 (`tests/server.IntegrationTests`, service postgres trong CI)
- **Ngày:** 2026-11-18

## Bối cảnh
Các tính năng dựa vào PostgreSQL: `jsonb`, `tsvector`, `SKIP LOCKED`, `xmin`. SQLite/in-memory không có.

## Quyết định
`WebApplicationFactory<Program>` + PostgreSQL thật. CI dùng `services: postgres:15` của GitHub Actions; máy dev dùng `docker compose up -d`. Chuỗi kết nối qua biến `TEST_CONNECTION_STRING`.

## Phương án đã cân nhắc và loại
SQLite in-memory (sai hành vi), Testcontainers (thư viện ngoài).

## Hệ quả
Test chậm hơn unit test. Test ghi dữ liệu phải tự dọn hoặc dùng transaction riêng (áp dụng từ PR-3).
