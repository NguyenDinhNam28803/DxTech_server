# Thiết kế triển khai module Content (CT-1 → CT-C2)

- **Trạng thái:** Đã duyệt 27/11/2026 · CT-1 đã làm (xem ghi chú triển khai cuối ADR-015) · **Ngày:** 27/11/2026 · **Quyết định:** ADR-015
- **Lịch (phương án B):** 07/12 → 20/12/2026, sau PR-9/10/11. Thứ tự: CT-1 → CT-2 → CT-3 → CT-C1 → CT-C2.

## 1. Bảng (schema `content`)

### 1.1 `topics` — cây chủ đề theo loại
| Cột | Kiểu | Ràng buộc | Ghi chú |
|---|---|---|---|
| id | uuid PK | | v7 |
| post_type | text | CHECK enum | news · solution · training · resource · glossary · page |
| parent_id | uuid NULL | FK → topics | cây 2–3 cấp (Giải pháp → Dịch vụ → Phát triển phần mềm) |
| slug | text | CHECK slug; UQ (post_type, parent_id, slug) chưa xoá | |
| name · description | text | 1–100 · ≤ 500 | |
| sort_order | int | | |
| is_active · deleted_at · xmin · created_at · updated_at | | | |

### 1.2 `posts`
| Cột | Kiểu | Ràng buộc | Ghi chú |
|---|---|---|---|
| id | uuid PK | | |
| post_type | text | CHECK enum | |
| topic_id | uuid NULL | FK → topics | cùng `post_type` (kiểm ở code) |
| slug | text | CHECK slug; **UQ (post_type, slug) WHERE deleted_at IS NULL** | không sinh lại từ tiêu đề |
| title | text | 1–200 | |
| excerpt | text NULL | ≤ 500 | thẻ tin, mô tả SEO mặc định |
| label | text NULL | ≤ 40 | “HƯỚNG DẪN”, “BẢO MẬT”… (tin tức) |
| blocks | jsonb | CHECK là mảng; ≤ 200 khối | ADR-015 §3, kiểm chi tiết ở code |
| key_points | jsonb | mảng chuỗi, ≤ 10 | “Ý chính” của bài |
| cover_media_id | uuid NULL | FK → media | |
| status | text | draft · published · archived | |
| published_at | timestamptz NULL | CHECK status = published ⇒ NOT NULL | tương lai = hẹn giờ |
| author_id · updated_by | uuid | FK → identity.users | |
| seo_title · seo_description | text NULL | ≤ 70 · ≤ 160 | |
| reading_minutes | smallint NULL | | tính từ số chữ khi lưu |
| sort_order · is_featured | int · bool | | trang chủ, thứ tự trong chủ đề |
| search_vector | tsvector | GENERATED STORED | tiêu đề (A) + tóm tắt (B) + chữ trong khối (C), không dấu |
| deleted_at · xmin · created_at · updated_at | | | |

Index: UQ slug; `(post_type, published_at DESC) WHERE status = 'published' AND deleted_at IS NULL` (danh sách công khai);
`(topic_id, sort_order)`; GIN `search_vector`; `(post_type, updated_at DESC)` cho admin.

### 1.3 Bảng phụ
| Bảng | Cột chính | Ghi chú |
|---|---|---|
| `post_revisions` | id bigint IDENTITY, post_id, title, blocks, key_points, saved_by, saved_at | Ghi mỗi lần lưu; giữ 50 bản gần nhất/bài (job dọn) |
| `slug_redirects` | post_type, old_slug (UQ theo loại), post_id | API chi tiết gặp slug cũ ⇒ trả `301` + slug mới |
| `media` | id, file_name (`<id>.jpg`), content_type CHECK jpeg/png/webp, bytes ≤ 5 MB, width, height, alt_default, sha256 (UQ — chống tải trùng), uploaded_by, created_at, deleted_at | File trên đĩa; DB chỉ metadata |
| `course_sessions` | id, post_id (training), starts_on, ends_on, format online/offline/hybrid, location, seats, price_vnd NULL, status open/full/closed | Lịch khai giảng hiển thị trên trang khoá học |
| `leads` | id, kind consultation/course, post_id NULL, session_id NULL, full_name, email NULL, phone NULL (CHECK có ít nhất một), company, message ≤ 2000, consent_version, consent_at, source_ip inet, user_agent, status new/contacted/closed, handled_by, created_at, anonymized_at | Ẩn danh hoá sau 24 tháng |

Quyền DB: `post_revisions` chỉ thêm (app có DELETE để dọn bản cũ — job); `leads` app không DELETE (ẩn danh hoá bằng UPDATE).

## 2. Khối nội dung (validation phía server)
```json
[
  { "id": "b1", "type": "heading", "level": 2, "text": "Bước 1: Kiểm tra thiết bị" },
  { "id": "b2", "type": "paragraph", "spans": [ { "text": "Đối chiếu " }, { "text": "phiên bản Windows", "bold": true },
                                               { "text": " trên trang", "href": "/san-pham/microsoft-365" } ] },
  { "id": "b3", "type": "list", "ordered": false, "items": [ [ { "text": "Dung lượng trống" } ], [ { "text": "Kết nối Internet" } ] ] },
  { "id": "b4", "type": "image", "mediaId": "0193…", "alt": "Màn hình kích hoạt", "caption": null },
  { "id": "b5", "type": "callout", "tone": "warn", "spans": [ { "text": "Không gửi mật khẩu qua chat." } ] },
  { "id": "b6", "type": "cta", "label": "Đăng ký tư vấn", "href": "/lien-he?topic=erp" },
  { "id": "b7", "type": "faq", "question": "Bao lâu thì kích hoạt?", "answer": [ { "text": "Ngay sau khi nhận key." } ] }
]
```
Luật: `id` duy nhất trong bài (sinh ở client, giữ nguyên khi sửa — làm khoá đoạn cho AI); `href` chỉ `/…` nội bộ hoặc `https://`;
`mediaId` phải tồn tại và chưa xoá; tổng chữ ≤ 50.000 ký tự/bài; không có trường lạ (từ chối, không bỏ qua).

## 3. API

### Công khai (`[AllowAnonymous]`, cache 60 giây ở Nginx)
| Method · Path | Kết quả |
|---|---|
| `GET /api/content/topics?type=solution` | Cây chủ đề (menu Giải pháp, trang Đào tạo) |
| `GET /api/content/posts?type=news&topic=&q=&cursor=` | Danh sách đã đăng, keyset; `q` tìm không dấu |
| `GET /api/content/posts/{type}/{slug}` | Chi tiết; slug cũ ⇒ 301 `Location` slug mới; chưa đăng ⇒ 404 |
| `GET /api/content/featured?type=training` | Thẻ nổi bật trang chủ (thay link gắn cứng ở `EditorialHero`) |
| `GET /api/content/courses/{slug}/sessions` | Lịch khai giảng đang mở |
| `POST /api/content/leads` | Đăng ký tư vấn/khoá học — rate limit 5/giờ/IP, honeypot; 202 |
| `GET /api/content/sitemap.xml` | Sitemap mọi bài đã đăng + sản phẩm |

### Quản trị (`policy content` = editor | admin)
| Method · Path | Ghi chú |
|---|---|
| `GET/POST /api/admin/content/posts`, `GET/PUT/DELETE …/{id}` | Lưu = tạo revision; `If-Match` theo xmin ⇒ 409 khi xung đột |
| `POST …/{id}/publish` `{publishedAt?}` · `POST …/{id}/unpublish` | Hẹn giờ = `publishedAt` tương lai |
| `PUT …/{id}/slug` | Ghi `slug_redirects` |
| `GET …/{id}/revisions` · `POST …/{id}/revisions/{rev}/restore` | |
| `GET/POST/PUT/DELETE /api/admin/content/topics` | |
| `POST /api/admin/content/media` (multipart) · `GET …/media?cursor=` · `DELETE …/media/{id}` | Xoá chặn nếu còn bài dùng |
| `GET/POST /api/admin/content/courses/{postId}/sessions` | |
| `GET /api/admin/content/leads?status=` · `PUT …/leads/{id}` | Đổi trạng thái, ghi chú |

## 4. Giao diện admin (CT-C2)
Tab **Nội dung** trong `/admin` (editor chỉ thấy tab này): danh sách lọc loại/trạng thái/chủ đề, tìm kiếm · form soạn theo khối
(thêm/xoá/kéo thả khối, xem trước đúng giao diện trang thật) · nút Lưu nháp / Đăng / Hẹn giờ · lịch sử phiên bản · thư viện ảnh ·
lịch khai giảng (bài đào tạo) · danh sách lead.

## 5. Lộ trình & kiểm thử

| PR | Nội dung | Ngày | Kiểm thử chính |
|---|---|---|---|
| CT-1 | Schema `content` + role `editor` (CHECK `ck_users_role`) + `--seed-content` + `--set-role` | 07–08/12 | Ràng buộc; seed giữ đúng 100% slug hiện có (so với danh sách URL đang chạy); chạy seed 2 lần không nhân đôi |
| CT-2 | API công khai + sitemap + 301 slug cũ + lead | 09–10/12 | Bản nháp/hẹn giờ không lộ; slug cũ ⇒ 301; lead spam bị chặn; tìm không dấu |
| CT-3 | API quản trị + revision + media (magic bytes, xoá EXIF) + Nginx `/media/` | 11–13/12 | Khối lạ/URL `javascript:` bị từ chối; SVG/ảnh đổi đuôi bị từ chối; ảnh có GPS ⇒ lưu ra không còn GPS; editor gọi API đơn hàng ⇒ 403; 409 khi sửa đồng thời |
| CT-C1 | Web đọc API (tin tức, giải pháp, đào tạo, tài nguyên, trang chủ), form lead | 14–16/12 | Playwright: mọi URL cũ vẫn mở được; nội dung khớp |
| CT-C2 | Tab Nội dung trong admin | 16–20/12 | Playwright: soạn → hẹn giờ → tự hiện đúng giờ; khôi phục phiên bản |

**Nghiệm thu module Content:** biên tập viên sửa/đăng bài không cần lập trình viên; mọi URL hiện có vẫn hoạt động; không có cách
chèn script qua nội dung hoặc ảnh; editor không truy cập được dữ liệu bán hàng; nội dung đọc được qua API (đầu vào cho AI tư vấn).

## 6. Câu hỏi mở (không chặn CT-1)
1. Ai trong công ty là biên tập viên? (tạo tài khoản role `editor`)
2. Văn bản đồng ý xử lý dữ liệu cho form lead — cần pháp chế duyệt (dùng chung với chính sách quyền riêng tư).
3. Có hiển thị học phí công khai cho khoá đào tạo không (cột `price_vnd` cho phép trống)?
