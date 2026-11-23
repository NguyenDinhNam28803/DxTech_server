# ADR-008: Tìm kiếm full-text tiếng Việt không dấu; cây danh mục adjacency list

- **Trạng thái:** Đã áp dụng — PR-3 (schema, `f_unaccent`, GIN); PR-4 (truy vấn tìm kiếm, CTE cây danh mục)
- **Ngày:** 2026-11-18

## Bối cảnh
Người dùng gõ "phan mem" phải tìm ra "Phần mềm"; cây danh mục nhỏ (<50 nút).

## Quyết định
Cột `search_vector tsvector` generated stored từ `catalog.f_unaccent(...)` (hàm bọc `unaccent` khai báo IMMUTABLE), cấu hình `simple`, index GIN. Danh mục dùng `parent_id` + CTE đệ quy, sâu tối đa 3.

## Phương án đã cân nhắc và loại
`ILIKE`, Elasticsearch; materialized path, `ltree`, nested set.

## Hệ quả
Cần extension `unaccent` (cài bằng superuser ở PR-2). Gợi ý khi gõ/tìm gần đúng: bổ sung `pg_trgm` sau.

## Ghi chú triển khai (PR-3, 21/11/2026)
- `catalog.f_unaccent(text)` viết dạng `RETURN public.unaccent('public.unaccent'::regdictionary, input)`.
  Không dùng thân `BEGIN ATOMIC … ; END`: Npgsql tách lệnh theo dấu `;` khi chạy migration và làm vỡ thân hàm.
- `search_vector` có trọng số: tên (A) > hãng (B) > mô tả ngắn (C), cấu hình `simple`.
- Câu truy vấn của người dùng cũng phải qua `catalog.f_unaccent` (PR-4) để "PHẦN MỀM" khớp "phan mem".
