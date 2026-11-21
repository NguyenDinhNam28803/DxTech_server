# ADR-009: Tách role database: owner (migrate) và app (API)

- **Trạng thái:** Đã áp dụng (PR-2)
- **Ngày:** 2026-11-18 (cập nhật 2026-11-20 khi triển khai)

## Bối cảnh
API kết nối bằng `POSTGRES_USER` — superuser của container (bypass mọi quyền, kể cả RLS, đọc được file
trên máy chủ qua `COPY ... FROM PROGRAM`). Một lỗi SQL injection hay một bug trong code lúc đó có thể
xoá bảng hoặc chiếm cả container database.

## Quyết định
| Role | Ai dùng | Quyền |
|---|---|---|
| `POSTGRES_USER` (superuser) | image postgres, service `db-roles`, admin/backup | Mọi thứ |
| `dxtechs_owner` | service `migrate` (EF migrations bundle) | Chủ database ⇒ chủ schema `public`; tạo schema/bảng; không superuser, không tạo DB/role |
| `dxtechs_app` | service `api` | `CONNECT`, `USAGE` schema, `SELECT/INSERT/UPDATE/DELETE` bảng, dùng sequence; chỉ **đọc** `__EFMigrationsHistory` |

- `deploy/postgres/roles.sql` (idempotent) chạy bằng superuser qua service `db-roles`
  **ở mọi lần deploy, trước `migrate`**: tạo role nếu chưa có, đặt lại thuộc tính + mật khẩu từ `.env`,
  chuyển chủ sở hữu đối tượng có sẵn sang `dxtechs_owner`, cấp quyền cho `dxtechs_app`,
  `ALTER DEFAULT PRIVILEGES FOR ROLE dxtechs_owner` để bảng/schema do migration tạo sau này tự có quyền.
- Tạo extension `unaccent` (cần cho ADR-008) ngay trong script vì cần superuser.
- Mật khẩu `DB_OWNER_PASSWORD`, `DB_APP_PASSWORD` trong `.env`, dạng hex (`openssl rand -hex 24`) để đưa
  thẳng vào connection string không cần escape. psql đọc chúng bằng `\getenv`, không qua dòng lệnh.

## Phương án đã cân nhắc và loại
- **Giữ một superuser cho mọi thứ:** đơn giản nhưng mọi lỗi ở API đều thành lỗi toàn quyền.
- **Init script `/docker-entrypoint-initdb.d`:** chỉ chạy khi volume rỗng ⇒ không áp được cho database
  đang chạy, và đổi mật khẩu sau này phải làm tay. Thay bằng `db-roles` chạy mỗi lần deploy.
- **Chạy `roles.sql` bằng tay một lần trên VPS** (dự kiến ban đầu): dễ lệch giữa môi trường, dễ quên
  khi tạo lại server. Tự động hoá qua compose rẻ hơn và kiểm thử được trong CI.
- **Phân quyền chi tiết theo từng bảng/schema module:** chưa cần ở giai đoạn modular monolith một API;
  xem lại khi tách service hoặc có job chỉ-đọc (báo cáo).

## Hệ quả
- Đổi mật khẩu role = sửa `.env` rồi deploy lại (hoặc `docker compose ... run --rm db-roles` rồi
  restart `migrate`/`api`).
- Thiếu `DB_OWNER_PASSWORD`/`DB_APP_PASSWORD` trong `.env` ⇒ `docker compose` báo lỗi trước khi làm gì,
  API cũ vẫn chạy.
- Migration không được dùng lệnh cần superuser (tạo extension, `ALTER SYSTEM`...): đưa vào `roles.sql`.
- Bảng `__EFMigrationsHistory` tạo lần đầu bởi `migrate` (database mới) sẽ nhận quyền ghi cho app qua
  default privileges; lần deploy kế tiếp `roles.sql` thu hồi lại.
- CI chạy `.github/scripts/test-db-roles.sh` trên PostgreSQL 15: kiểm tra quyền được phép và bị cấm
  của từng role, idempotent, đổi mật khẩu.
