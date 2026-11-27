# ADR-015: Module Content — quản trị tin tức, giải pháp, đào tạo ngay trong hệ thống

- **Trạng thái:** Đã duyệt (27/11/2026, phương án B)
- **Ngày:** 2026-11-27
- **Liên quan:** ADR-001 (modular monolith), ADR-002 (một DbContext), ADR-006 (id, thời gian), ADR-007 (xoá mềm, xmin),
  ADR-008 (tìm kiếm không dấu), ADR-009 (role DB), ADR-012 (role, policy); thiết kế: `docs/design/ct-content-module.md`

## Bối cảnh
Toàn bộ nội dung web nằm cứng trong code client: 6 bài tin (`articles.ts`), ~60 mục giải pháp/đào tạo (`solutions.ts`, URL sinh
từ tên — đổi tên là gãy link), tài nguyên (`resources.ts`), 6 link đào tạo gắn cứng ở trang chủ. Admin không có tab nội dung.
Hệ quả: sửa một chữ cần lập trình viên + deploy; không bản nháp, không lịch sử; công cụ tìm kiếm đọc kém; AI tư vấn (sau CMS)
không có nguồn dữ liệu. Phương án B (27/11) đặt CMS trước AI tư vấn.

## Quyết định

### 1. Vị trí
Module `Content` trong server hiện có, schema PostgreSQL `content` — không thêm dịch vụ CMS riêng.

### 2. Mô hình nội dung
| Bảng | Vai trò |
|---|---|
| `topics` | Cây chủ đề theo loại nội dung (Giải pháp → Dịch vụ / Tư vấn triển khai / Đào tạo; nhãn tin tức) |
| `posts` | Mọi nội dung: `news`, `solution`, `training`, `resource`, `glossary`, `page` |
| `post_revisions` | Ảnh chụp mỗi lần lưu — xem lại, khôi phục |
| `slug_redirects` | Đổi slug ⇒ 301 từ URL cũ |
| `media` | Ảnh tải lên (lưu đĩa VPS, Nginx phục vụ) |
| `course_sessions` | Lịch khai giảng của bài `training` |
| `leads` | Đăng ký tư vấn / khoá học (có đồng ý xử lý dữ liệu — NĐ 13/2023) |

- **Slug cố định**, nhập tay hoặc sinh một lần khi tạo; **không** sinh lại từ tiêu đề. UNIQUE (type, slug) trong bài chưa xoá.
- **Trạng thái:** `draft` · `published` · `archived`. **Hẹn giờ không cần job:** bài hiển thị khi
  `status = 'published' AND published_at <= now()` — đặt `published_at` trong tương lai là hẹn giờ.
- Xoá mềm (`deleted_at`) + `xmin` (hai người sửa cùng bài ⇒ người sau nhận 409).

### 3. Nội dung dạng khối có cấu trúc (không HTML tự do)
`posts.blocks` là mảng JSON các khối thuộc danh sách trắng: `heading` (cấp 2–3), `paragraph`, `list` (có/không thứ tự),
`image` (tham chiếu `media.id` + alt bắt buộc), `callout` (info/warn), `quote`, `cta` (nhãn + đường dẫn nội bộ hoặc https),
`faq` (câu hỏi + trả lời). Định dạng trong đoạn chỉ có **đậm**, *nghiêng*, liên kết — lưu dạng mảng "đoạn chữ" có cờ, không phải HTML.
Server kiểm tra từng khối (loại, độ dài, URL chỉ `https:` hoặc đường dẫn nội bộ, ảnh phải tồn tại) trước khi lưu.
- **Được:** chống XSS từ gốc (Vue tự escape; không có chỗ chèn HTML), không cần thư viện ngoài (giữ ràng buộc gói Microsoft),
  khớp cấu trúc `sections` của `articles.ts`, **mỗi khối có `id` ổn định = một đoạn cho AI tư vấn** (ADR sau).
- **Mất:** soạn kém tự do hơn trình soạn kiểu Word; cần làm form soạn theo khối.

### 4. Quyền
- Thêm role **`editor`** (sửa ADR-012 §5: `customer | editor | admin`): quản lý nội dung, media, xem lead;
  **không** thấy đơn hàng, thanh toán, key, người dùng. Policy `content` = editor hoặc admin; policy `admin` giữ nguyên.
- Admin tạo/đổi role editor qua CLI (`--set-role <email> editor`) — giống `--create-admin`, không có API tự nâng quyền.
- API công khai chỉ trả bài đã đăng; bản nháp chỉ qua API quản trị.

### 5. Media
- Chỉ nhận **JPEG, PNG, WebP**, ≤ 5 MB, ≤ 6000×6000; xác định loại bằng **magic bytes**, không tin phần mở rộng hay Content-Type.
  **Không nhận SVG** (chứa được script). Xoá metadata (EXIF/GPS) bằng cách bỏ các segment/chunk metadata — không giải mã ảnh,
  không thư viện ngoài.
- Tên file = `media.id` (UUID v7) + đuôi chuẩn; lưu `/var/www/dxtechs/media/yyyy/mm/`; Nginx phục vụ `https://dxtechs.com/media/…`
  với `Content-Type` cố định theo đuôi, `X-Content-Type-Options: nosniff`, cache dài hạn (tên file không bao giờ đổi).
- Ảnh ngoài (Unsplash hiện tại) được tải về và nhập vào `media` khi seed — không phụ thuộc site khác.

### 6. Lead (đăng ký tư vấn / khoá học)
Form công khai, không cần đăng nhập: họ tên, email/điện thoại (ít nhất một), công ty, nhu cầu, bài/khoá học liên quan,
**ô đồng ý bắt buộc** (lưu phiên bản văn bản đồng ý + thời điểm). Chống spam: rate limit theo IP, trường bẫy (honeypot),
từ chối gửi quá nhanh sau khi mở form. Giữ 24 tháng rồi ẩn danh hoá. Thông báo email khi có nhà cung cấp mail (Phase 4).

### 7. Tìm kiếm & SEO
- Tìm kiếm không dấu giống Catalog: cột `search_vector` sinh từ tiêu đề + tóm tắt + chữ trong khối (hàm IMMUTABLE trích chữ).
- `GET /api/content/sitemap.xml` (Nginx trỏ `dxtechs.com/sitemap.xml`); trường `seo_title`, `seo_description`, ảnh chia sẻ.
- Thẻ meta cho bot mạng xã hội/tìm kiếm trên SPA: làm ở Phase 5 (prerender bài viết) — ngoài phạm vi ADR này.

### 8. Chuyển dữ liệu hiện có
Lệnh `--seed-content` đọc bản xuất JSON của `articles.ts`, `solutions.ts`, `resources.ts`, giữ **đúng slug đang chạy**
(cả slug sinh từ tên, ví dụ `ai-cho-doanh-nghiep-x10-hieu-suat`) ⇒ không đổi URL. Chạy một lần, chỉ thêm cái còn thiếu (như ADR-010).

## Phương án đã cân nhắc và loại
| Phương án | Vì sao không chọn |
|---|---|
| CMS riêng (Strapi, Directus, Payload…) | Thêm một dịch vụ Node.js + DB/role/backup/bản vá riêng trên một VPS; hai hệ thống phân quyền; khó cùng transaction và cùng nguồn cho AI |
| Giữ WordPress cũ cho nội dung | Một hệ thống nữa phải vá bảo mật; tách rời tài khoản; công ty chưa quyết số phận website cũ |
| Markdown + thư viện chuyển HTML | Cần gói ngoài (Markdig) + bộ lọc HTML chống XSS; Markdown cho phép HTML thô |
| HTML tự do từ trình soạn WYSIWYG | Rủi ro XSS lớn nhất; cần sanitizer phía server; khó chia đoạn cho AI |
| Nội dung trong file JSON của repo client | Vẫn cần lập trình viên + deploy; không phân quyền, không lịch sử |

## Hệ quả
- ADR-012: thêm role `editor` (migration sửa CHECK `ck_users_role`), policy `content`.
- Nginx: thêm `location /media/` và `/sitemap.xml` (script có kiểm tra + quay lại như `d2-nginx.sh`).
- Backup: thư mục media cần sao lưu cùng DB (script backup mở rộng, mã hoá như dump).
- Web: các trang tin tức/giải pháp/đào tạo/tài nguyên đọc API; URL giữ nguyên.
