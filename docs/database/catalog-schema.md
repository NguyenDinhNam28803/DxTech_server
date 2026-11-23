# Schema `catalog` — thiết kế & phân tích

- **Cập nhật:** 23/11/2026 (PR-4) · **Migration:** `20261121021354_Catalog_Initial` (PR-3)
- **SQL đầy đủ:** [`docs/migrations/20261121021354_Catalog_Initial.sql`](../migrations/20261121021354_Catalog_Initial.sql)
- **Quyết định liên quan:** ADR-002 (schema theo module), 006 (đặt tên, id, tiền, thời gian), 007 (xoá mềm, xmin), 008 (tìm kiếm, cây danh mục), 009 (role)

## 1. Sơ đồ quan hệ

```
catalog.categories                     catalog.products                         catalog.product_variants
──────────────────                     ────────────────                         ────────────────────────
id            uuid PK (v7)  ◄──┐       id               uuid PK (v7)   ◄──┐     id                    uuid PK (v7)
parent_id     uuid FK ───────┘ │       category_id      uuid FK NOT NULL ─┘ │   product_id            uuid FK NOT NULL ─┘
slug          text             └────── slug             text                 │   sku                   text
name          text                     name             text                 │   name                  text
description   text NULL                short_description text NULL           │   term_months           int NULL (NULL = vĩnh viễn)
sort_order    int  = 0                 description      text NULL            │   seats                 int = 1
is_active     bool = true              brand            text NULL            │   price_vnd             bigint
created_at    timestamptz              license_type     text                 │   compare_at_price_vnd  bigint NULL
updated_at    timestamptz              platforms        text[] = '{}'        │   delivery_type         text
deleted_at    timestamptz NULL         features         jsonb  = '[]'        │   is_default            bool
                                       image_url, badge text NULL            │   sort_order            int
  1 danh mục ── n danh mục con         is_active        bool = true          │   is_active             bool = true
  1 danh mục ── n sản phẩm             search_vector    tsvector GENERATED   │   xmin (hệ thống)       xid — concurrency token
                                       created_at / updated_at / deleted_at  │   created_at / updated_at / deleted_at
                                                                             │
                                         1 sản phẩm ── n gói (variant) ──────┘
```

Mọi khoá ngoại `ON DELETE RESTRICT`: không xoá cứng được dòng cha khi còn dòng con — muốn bỏ thì **xoá mềm** (`deleted_at`).

## 2. Ràng buộc trong database (lớp phòng thủ cuối)

| Bảng | Ràng buộc | Chặn được gì |
|---|---|---|
| categories | `ck_categories_slug`: `^[a-z0-9]+(-[a-z0-9]+)*$` | Slug có dấu, chữ hoa, dấu cách, gạch đầu/cuối — URL luôn sạch |
| categories | `ck_categories_name`: 1–100 ký tự | Tên rỗng, tên dài phá giao diện |
| categories | `ck_categories_not_own_parent` | Danh mục tự làm cha của chính nó |
| products | `ck_products_slug`, `ck_products_name` (1–200) | Như trên |
| products | `ck_products_license_type` ∈ perpetual, subscription, service | Giá trị enum lạ |
| products | `ck_products_platforms`: `platforms <@ {windows, macos, linux, ios, android, web}` | Gõ sai nền tảng ("Windows") làm bộ lọc trượt |
| products | `ck_products_features_array`: `jsonb_typeof(features) = 'array'` | Ghi object/chuỗi vào cột danh sách |
| variants | `ck_product_variants_price`: `price_vnd >= 0` | Giá âm |
| variants | `ck_product_variants_compare_at_price`: NULL hoặc `> price_vnd` | "Giá gốc" thấp hơn giá bán (giảm giá giả) |
| variants | `term_months > 0`, `seats > 0`, SKU `^[A-Za-z0-9][A-Za-z0-9._-]*$` | Gói 0 tháng / 0 người dùng; SKU có dấu cách |
| variants | `ck_product_variants_delivery_type` ∈ license_key, account, manual | Luồng giao hàng không xác định (Phase 3) |

Ràng buộc nằm trong DB chứ không chỉ trong code C#, vì dữ liệu còn được ghi bằng SQL tay, seeder, và các module sau này: DB là nơi duy nhất mọi đường ghi đều phải đi qua.

## 3. Index — mỗi index phục vụ một truy vấn thật

| Index | Kiểu | Truy vấn phục vụ |
|---|---|---|
| `ix_categories_slug` UNIQUE `WHERE deleted_at IS NULL` | btree | Tìm danh mục theo slug (lọc danh sách); slug của bản đã xoá dùng lại được |
| `ix_categories_parent_id` | btree | CTE đệ quy lấy danh mục con; kiểm tra FK khi xoá |
| `ix_products_slug` UNIQUE `WHERE deleted_at IS NULL` | btree | `GET /api/products/{slug}` (0,02 ms trên 2.000 sản phẩm) |
| `ix_products_category_id_listed` `WHERE deleted_at IS NULL AND is_active` | btree (partial) | Lọc theo danh mục — chỉ chứa sản phẩm đang bán nên nhỏ hơn index đầy đủ |
| `ix_products_platforms` | GIN | `platforms @> ARRAY['macos']` — lọc hệ điều hành |
| `ix_products_search_vector` | GIN | `search_vector @@ to_tsquery(...)` — tìm kiếm |
| `ix_product_variants_product_id_price_vnd` | btree | Giá thấp nhất mỗi sản phẩm, lọc khoảng giá, `EXISTS` gói đang bán |
| `ix_product_variants_sku` UNIQUE `WHERE deleted_at IS NULL` | btree | Trùng SKU |
| `ux_product_variants_one_default` UNIQUE (product_id) `WHERE is_default AND deleted_at IS NULL` | btree (partial) | Mỗi sản phẩm tối đa **một** gói mặc định |

## 4. Tìm kiếm tiếng Việt không dấu

1. `catalog.f_unaccent(text)` bọc `unaccent()` thành `IMMUTABLE` (bản gốc chỉ `STABLE` ⇒ không dùng được trong generated column/index).
2. `search_vector` = `setweight(tên,'A') || setweight(hãng,'B') || setweight(mô tả ngắn,'C')`, cấu hình `simple` (không stemming, chỉ hạ chữ thường), tính sẵn khi ghi (generated stored).
3. Câu người dùng gõ → `SearchText.ToTsQuery`: chỉ giữ chữ/số, mỗi từ khớp tiền tố, nối AND (`"phan me"` → `phan:* & me:*`); bỏ dấu ở SQL bằng cùng hàm `f_unaccent`.
4. Xếp hạng `ts_rank` theo trọng số: khớp tên đứng trước khớp mô tả.

Vì `f_unaccent` là IMMUTABLE nên planner **gập hằng số** ngay lúc lập kế hoạch: `to_tsquery('simple', f_unaccent('đồ:* & hoạ:*'))` thành hằng `'do':* & 'hoa':*` — không tốn chi phí mỗi dòng.

## 5. Quy tắc "sản phẩm đang bán"

Một sản phẩm xuất hiện ở danh sách, tìm kiếm và trang chi tiết khi **đồng thời**: chưa xoá mềm, `is_active`, danh mục chưa xoá và `is_active`, có ít nhất một gói chưa xoá và `is_active`. Chung một chỗ: `Features/Products/ProductListing.Listed`.

Giá trên thẻ sản phẩm lấy từ **gói rẻ nhất đang bán**: `minPriceVnd` + `compareAtPriceVnd` của chính gói đó (không ghép giá gói này với giá gốc gói khác).

## 6. Hiệu năng đo được (PostgreSQL 16 cục bộ, 2.000 sản phẩm, 6.000 gói)

| Truy vấn | Thời gian | Kế hoạch thực thi |
|---|---|---|
| Danh sách: danh mục + nền tảng + khoảng giá, sắp theo giá, trang 12 | ~3,7 ms | Bitmap Index Scan trên `ix_products_platforms`; Index Scan `(product_id, price_vnd)` cho EXISTS/MIN |
| Tìm kiếm "đồ hoạ" (400 kết quả, lấy 12) | ~1,8 ms | Bảng nhỏ ⇒ planner chọn Seq Scan; ép tắt seq scan ⇒ Bitmap Index Scan trên GIN |
| Chi tiết theo slug | ~0,02 ms | Index Scan `ix_products_slug` |

Đã sửa trong PR-4: chiếu "gói rẻ nhất" dạng object khiến EF sinh `ROW_NUMBER()` trên **toàn bộ** bảng gói (chi phí tăng theo tổng số gói, ~7 ms) → đổi thành hai subquery tương quan chỉ tính cho các dòng của trang (~3,7 ms).

## 7. Giới hạn đã biết & khi nào xem lại

| Giới hạn | Ngưỡng xem lại | Hướng xử lý |
|---|---|---|
| Phân trang OFFSET chậm dần ở trang sâu | > ~10.000 sản phẩm hoặc trang > 100 | Keyset pagination (đã có trong thiết kế cho đơn hàng) |
| `MIN(price)` tính khi đọc | Danh sách > 20 ms | Cột `min_price_vnd` phi chuẩn hoá, cập nhật khi ghi gói |
| `COUNT(*)` cho tổng số | Bảng rất lớn | Ước lượng từ `pg_class.reltuples` hoặc bỏ tổng chính xác |
| Không tìm được lỗi chính tả ("phan mm") | Khi có phản hồi người dùng | `pg_trgm` + similarity (cần duyệt extension) |
| Slug `search` dành cho route `/api/products/search` | — | Không đặt slug sản phẩm là `search` |
