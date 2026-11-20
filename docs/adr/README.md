# Architecture Decision Records (ADR)

Mỗi file ghi một quyết định kiến trúc: bối cảnh, quyết định, phương án đã loại, hệ quả.
Chi tiết so sánh và sơ đồ: tài liệu "DXTechs — Thiết kế Server & Database" v1.0 (18/11/2026).

| ADR | Quyết định | Trạng thái |
|---|---|---|
| [001](001-modular-monolith.md) | Modular monolith dạng gọn trong một project | Đã áp dụng — PR-1 (khung `Modules/`, test kiến trúc) |
| [002](002-single-dbcontext-schema-per-module.md) | Một AppDbContext, mỗi module một schema PostgreSQL | Đã chấp nhận — `AppDbContext` ở PR-1, schema đầu tiên (`catalog`) ở PR-3 |
| [003](003-handlers-dbcontext-manual-mapping.md) | Handler gọi trực tiếp, DbContext trực tiếp, map tay | Đã chấp nhận — áp dụng từ PR-4 |
| [004](004-result-problem-details.md) | Result<T> + ProblemDetails cho lỗi nghiệp vụ | Đã áp dụng — PR-1 (`Shared/Results`, `Infrastructure/Http/ResultHttpExtensions`) |
| [005](005-integration-tests-real-postgres.md) | Integration test với PostgreSQL thật | Đã áp dụng — PR-1 (`tests/server.IntegrationTests`, service postgres trong CI) |
| [006](006-naming-ids-money-time.md) | snake_case, UUID v7, tiền bigint VND, enum text, timestamptz UTC | Một phần — PR-1 (snake_case, UuidV7, IAuditable + TimeProvider); cột tiền/enum ở PR-3 |
| [007](007-soft-delete-and-concurrency.md) | Soft delete chỉ cho catalog; concurrency bằng xmin | Đã chấp nhận — áp dụng ở PR-3 |
| [008](008-full-text-search-and-category-tree.md) | Tìm kiếm full-text tiếng Việt không dấu; cây danh mục adjacency list | Đã chấp nhận — áp dụng ở PR-3/PR-4 |
| [009](009-database-roles.md) | Tách role database: owner (migrate) và app (API) | Đã chấp nhận — áp dụng ở PR-2 (cần thao tác trên VPS) |
| [010](010-demo-seed-on-demand.md) | Dữ liệu demo chạy theo lệnh | Đã chấp nhận — áp dụng ở PR-5 |
| [011](011-api-subdomain.md) | API trên subdomain riêng `api.dxtechs.com` | Đã áp dụng — hạ tầng 20/11/2026; CORS ở PR-C1 |

## Thêm ADR mới
Tạo file `NNN-ten-ngan.md` theo cùng khuôn. Không sửa nội dung một ADR đã chấp nhận: muốn đổi thì viết ADR mới và ghi "Thay thế ADR-NNN".
