# ADR-008: Tìm kiếm full-text tiếng Việt không dấu; cây danh mục adjacency list

- **Trạng thái:** Đã chấp nhận — áp dụng ở PR-3/PR-4
- **Ngày:** 2026-11-18

## Bối cảnh
Người dùng gõ "phan mem" phải tìm ra "Phần mềm"; cây danh mục nhỏ (<50 nút).

## Quyết định
Cột `search_vector tsvector` generated stored từ `catalog.f_unaccent(...)` (hàm bọc `unaccent` khai báo IMMUTABLE), cấu hình `simple`, index GIN. Danh mục dùng `parent_id` + CTE đệ quy, sâu tối đa 3.

## Phương án đã cân nhắc và loại
`ILIKE`, Elasticsearch; materialized path, `ltree`, nested set.

## Hệ quả
Cần extension `unaccent` (cài bằng superuser ở PR-2). Gợi ý khi gõ/tìm gần đúng: bổ sung `pg_trgm` sau.
