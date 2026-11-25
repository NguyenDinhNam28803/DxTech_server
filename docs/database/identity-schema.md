# Schema `identity` — tài khoản & phiên đăng nhập

- **Cập nhật:** 24/11/2026 (PR-7) · **Migration:** `20261124233715_Identity_Initial`
- **SQL đầy đủ:** [`docs/migrations/20261124233715_Identity_Initial.sql`](../migrations/20261124233715_Identity_Initial.sql)
- **Quyết định:** ADR-012 (xác thực), ADR-006 (id, thời gian), ADR-009 (role DB); thiết kế: *Thiết kế toàn bộ Database v1.0*, mục 6

## 1. Bảng

```
identity.users                                   identity.refresh_tokens
──────────────                                   ───────────────────────
id                 uuid PK (v7)   ◄──────────┐   id                  uuid PK (v7)
email              citext UNIQUE              └── user_id             uuid FK ON DELETE CASCADE
email_confirmed_at timestamptz NULL              token_hash          bytea(32) UNIQUE  — SHA-256, không lưu token gốc
password_hash      text           — PBKDF2       family_id           uuid      — cả chuỗi xoay vòng từ 1 lần đăng nhập
full_name          text (1–100)                  replaced_by_id      uuid FK → refresh_tokens.id NULL
phone              text NULL  ^\+?[0-9]{8,15}$   expires_at          timestamptz — trượt 14 ngày
company_name       text NULL (1–200)             session_expires_at  timestamptz — tuyệt đối 30 ngày (ADR-012)
tax_code           text NULL  10 | 13 số          revoked_at / revoked_reason      — cùng NULL hoặc cùng có
role               text  customer | admin        created_ip          inet NULL
status             text  active | locked | disabled   user_agent     text NULL (≤ 512)
failed_login_count int ≥ 0                       created_at          timestamptz
lockout_until      timestamptz NULL
security_stamp     uuid (v4 ngẫu nhiên)
last_login_at      timestamptz NULL
xmin               xid — concurrency token
created_at / updated_at
```

## 2. Vì sao như vậy

| Quyết định | Lý do | Đánh đổi |
|---|---|---|
| `email citext` + UNIQUE | `A@X.com` = `a@x.com` ngay trong DB, mọi truy vấn không cần nhớ `lower()` | Cần extension `citext` — tạo bằng superuser trong `roles.sql` trước khi migrate |
| Không xoá mềm users | Khoá = `status`; yêu cầu xoá dữ liệu (NĐ 13) = ẩn danh hoá, giữ `id` cho đơn hàng | Không “khôi phục” được tài khoản đã ẩn danh |
| `security_stamp` UUID v4, không phải v7 | Giá trị dùng để vô hiệu hoá phiên ⇒ phải không đoán được; v7 có 48 bit đầu là thời gian | — |
| `refresh_tokens` CASCADE theo user | Dữ liệu kỹ thuật, không phải chứng từ | Khác quy tắc RESTRICT mặc định — có chủ đích |
| `token_hash` 32 byte + CHECK độ dài | Lộ DB không dùng được token; chặn ghi nhầm token gốc | Mất token ⇒ đăng nhập lại (đúng ý) |
| `session_expires_at` trên mỗi token | Kiểm tra hạn 30 ngày không cần truy vấn cả họ token | Lặp giá trị trong họ (có chủ đích) |

## 3. Index

| Index | Định nghĩa | Phục vụ |
|---|---|---|
| `ux_users_email` | UNIQUE (email) | Đăng nhập, chống đăng ký trùng |
| `ix_users_created_at` | (created_at DESC, id DESC) | Danh sách khách trong admin, keyset |
| `ix_users_role_admin` | (role) WHERE role = 'admin' | Liệt kê admin |
| `ux_refresh_tokens_token_hash` | UNIQUE (token_hash) | `POST /api/auth/refresh` |
| `ix_refresh_tokens_user_active` | (user_id) WHERE revoked_at IS NULL | Đăng xuất mọi nơi, danh sách phiên |
| `ix_refresh_tokens_family_id` | (family_id) | Thu hồi cả họ khi phát hiện dùng lại |
| `ix_refresh_tokens_expires_at` | (expires_at) | Job dọn token hết hạn |
| `ix_refresh_tokens_replaced_by_id` | (replaced_by_id) | Kiểm tra FK |

## 4. Kiểm thử (CI)
- Integration (`tests/server.IntegrationTests/Identity`): UNIQUE không phân biệt hoa thường, 9 CHECK của users, 7 CHECK của
  refresh_tokens, index + điều kiện lọc, cascade, `xmin`, lệnh `--create-admin` (hash kiểm được, không tự nâng quyền,
  mật khẩu yếu bị từ chối, không in mật khẩu, mã thoát).
- `test-db-roles.sh`: migration chạy bằng `dxtechs_owner`; `dxtechs_app` ghi được `identity.users`, citext chặn email trùng,
  app không DROP được bảng.
