# Architecture Decision Records (ADR)

Mỗi file ghi một quyết định kiến trúc: bối cảnh, quyết định, phương án đã loại, hệ quả.
Chi tiết so sánh và sơ đồ: tài liệu "DXTechs — Thiết kế Server & Database" v1.0 (18/11/2026).

| ADR | Quyết định | Trạng thái |
|---|---|---|
| [001](001-modular-monolith.md) | Modular monolith dạng gọn trong một project | Đã áp dụng — PR-1 (khung `Modules/`, test kiến trúc); PR-4: `Features/` được public (xem ghi chú) |
| [002](002-single-dbcontext-schema-per-module.md) | Một AppDbContext, mỗi module một schema PostgreSQL | Đã áp dụng — `AppDbContext` ở PR-1; schema `catalog` (3 bảng) ở PR-3 |
| [003](003-handlers-dbcontext-manual-mapping.md) | Handler gọi trực tiếp, DbContext trực tiếp, map tay | Đã áp dụng — PR-4 (Catalog: `GetCategoryTree`, `ListProducts`, `GetProductBySlug`) |
| [004](004-result-problem-details.md) | Result<T> + ProblemDetails cho lỗi nghiệp vụ | Đã áp dụng — PR-1 (`Shared/Results`, `Infrastructure/Http/ResultHttpExtensions`) |
| [005](005-integration-tests-real-postgres.md) | Integration test với PostgreSQL thật | Đã áp dụng — PR-1 (`tests/server.IntegrationTests`, service postgres trong CI) |
| [006](006-naming-ids-money-time.md) | snake_case, UUID v7, tiền bigint VND, enum text, timestamptz UTC | Đã áp dụng — PR-1 (snake_case, UuidV7, IAuditable + TimeProvider); PR-3 (`price_vnd bigint`, enum text + CHECK qua `SnakeCaseEnumConverter`) |
| [007](007-soft-delete-and-concurrency.md) | Soft delete chỉ cho catalog; concurrency bằng xmin | Đã áp dụng — PR-3 (`ISoftDeletable` + query filter + unique index `WHERE deleted_at IS NULL`; `xmin` trên `product_variants`) |
| [008](008-full-text-search-and-category-tree.md) | Tìm kiếm full-text tiếng Việt không dấu; cây danh mục adjacency list | Đã áp dụng — PR-3 (schema, `f_unaccent`, GIN); PR-4 (truy vấn tìm kiếm, CTE cây danh mục) |
| [009](009-database-roles.md) | Tách role database: owner (migrate) và app (API) | Đã áp dụng (PR-2) |
| [010](010-demo-seed-on-demand.md) | Dữ liệu demo chạy theo lệnh | Đã áp dụng — PR-5 (`--seed-demo`, chỉ thêm cái còn thiếu) |
| [011](011-api-subdomain.md) | API trên subdomain riêng `api.dxtechs.com` | Đã áp dụng — hạ tầng 20/11/2026; CORS ở PR-C1 |
| [012](012-authentication.md) | Xác thực: JWT 15 phút trong bộ nhớ + refresh token xoay vòng (cookie HttpOnly), PBKDF2, rate limit, phân quyền an toàn mặc định | Đã áp dụng — PR-7 (schema `identity`, `--create-admin`), PR-8 (API xác thực, policy Admin, rate limit) |

## Thêm ADR mới
Tạo file `NNN-ten-ngan.md` theo cùng khuôn. Không sửa nội dung một ADR đã chấp nhận: muốn đổi thì viết ADR mới và ghi "Thay thế ADR-NNN".
