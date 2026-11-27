#!/usr/bin/env bash
# Kiểm thử deploy/postgres/roles.sql (ADR-009) trên PostgreSQL thật.
#
# Cách chạy:
#   PGHOST=localhost PGPORT=5432 PGUSER=<superuser> PGPASSWORD=<...> .github/scripts/test-db-roles.sh
# Mặc định dùng psql trên máy; đặt DB_ROLES_PSQL_IMAGE=postgres:15 để chạy psql trong container
# (CI dùng cách này để client khớp phiên bản production).
#
# Script tạo database tạm roles_test_<pid> và xoá khi kết thúc. Role dxtechs_owner/dxtechs_app là
# role của cả cluster: chỉ chạy trên PostgreSQL dùng cho test, KHÔNG chạy trên production.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
sql_file="$repo_root/deploy/postgres/roles.sql"
db="roles_test_$$"
owner_pw="$(openssl rand -hex 24)"
app_pw="$(openssl rand -hex 24)"
failures=0

# psql_as <user> <password> <database> [psql args...]  — đọc SQL từ stdin.
psql_as() {
  local user="$1" password="$2" database="$3"
  shift 3
  if [ -n "${DB_ROLES_PSQL_IMAGE:-}" ]; then
    docker run --rm -i --network host \
      -e PGHOST="${PGHOST:-localhost}" -e PGPORT="${PGPORT:-5432}" \
      -e PGUSER="$user" -e PGPASSWORD="$password" -e PGDATABASE="$database" \
      -e DB_OWNER_PASSWORD -e DB_APP_PASSWORD \
      "$DB_ROLES_PSQL_IMAGE" psql -X -q -v ON_ERROR_STOP=1 "$@"
  else
    PGUSER="$user" PGPASSWORD="$password" PGDATABASE="$database" \
      psql -X -q -v ON_ERROR_STOP=1 "$@"
  fi
}

super() { psql_as "$PGUSER" "$PGPASSWORD" "$1" "${@:2}"; }

run_roles() { super "$db" < "$sql_file"; }

pass() { echo "  ✔ $1"; }
fail() { echo "  ✘ $1"; failures=$((failures + 1)); }

# expect_ok <mô tả> <user> <password> <sql>
expect_ok() {
  if psql_as "$2" "$3" "$db" -c "$4" >/dev/null 2>&1; then pass "$1"; else fail "$1"; fi
}
expect_denied() {
  if psql_as "$2" "$3" "$db" -c "$4" >/dev/null 2>&1; then fail "$1 (lẽ ra phải bị từ chối)"; else pass "$1"; fi
}
expect_value() { # <mô tả> <giá trị mong đợi> <sql chạy bằng superuser>
  local got
  got="$(super "$db" -tA -c "$3")"
  if [ "$got" = "$2" ]; then pass "$1"; else fail "$1 — mong đợi '$2', nhận '$got'"; fi
}

cleanup() { super postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE)" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "== Chuẩn bị: database $db giống production hiện tại (mọi thứ thuộc superuser)"
super postgres -c "CREATE DATABASE $db"
super "$db" <<'SQL'
CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
INSERT INTO public."__EFMigrationsHistory" VALUES ('00000000000000_Init', '8.0.8');
CREATE SCHEMA legacy;
CREATE TABLE legacy.notes (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, body text);
CREATE SEQUENCE legacy.free_seq;
SQL

echo "== Đầu vào sai phải bị từ chối"
if DB_OWNER_PASSWORD="$owner_pw" run_roles >/dev/null 2>&1; then fail "thiếu DB_APP_PASSWORD"; else pass "thiếu DB_APP_PASSWORD"; fi
if DB_OWNER_PASSWORD=short DB_APP_PASSWORD="$app_pw" run_roles >/dev/null 2>&1; then fail "mật khẩu quá ngắn"; else pass "mật khẩu quá ngắn"; fi
if DB_OWNER_PASSWORD="$app_pw" DB_APP_PASSWORD="$app_pw" run_roles >/dev/null 2>&1; then fail "hai mật khẩu trùng nhau"; else pass "hai mật khẩu trùng nhau"; fi

echo "== Chạy roles.sql 2 lần (idempotent)"
export DB_OWNER_PASSWORD="$owner_pw" DB_APP_PASSWORD="$app_pw"
run_roles
run_roles
pass "chạy lại lần 2 không lỗi"

echo "== Trạng thái sau khi chạy"
expect_value "chủ database là dxtechs_owner" "dxtechs_owner" \
  "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"
expect_value "__EFMigrationsHistory thuộc dxtechs_owner" "dxtechs_owner" \
  "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'public.\"__EFMigrationsHistory\"'::regclass"
expect_value "schema legacy + bảng + sequence identity + sequence rời thuộc dxtechs_owner" "4" \
  "SELECT (SELECT count(*) FROM pg_namespace WHERE nspname = 'legacy' AND nspowner = 'dxtechs_owner'::regrole)
        + (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'legacy' AND c.relkind IN ('r','S') AND c.relowner = 'dxtechs_owner'::regrole)"
expect_value "role không phải superuser, không tạo được DB/role" "f|f|f|f|f|f" \
  "SELECT string_agg(concat_ws('|', rolsuper, rolcreatedb, rolcreaterole), '|' ORDER BY rolname)
     FROM pg_roles WHERE rolname IN ('dxtechs_owner', 'dxtechs_app')"
expect_value "extension unaccent đã cài" "1" "SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'"

echo "== dxtechs_owner (container migrate)"
expect_ok "tạo schema + bảng mới (như EF migration)" dxtechs_owner "$owner_pw" \
  "CREATE SCHEMA sandbox; CREATE TABLE sandbox.products (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL)"
expect_ok "ghi lịch sử migration" dxtechs_owner "$owner_pw" \
  "INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('00000000000001_Catalog', '8.0.8')"
expect_ok "sửa bảng có sẵn (legacy.notes)" dxtechs_owner "$owner_pw" "ALTER TABLE legacy.notes ADD COLUMN extra text"
expect_denied "tạo database" dxtechs_owner "$owner_pw" "CREATE DATABASE nope_$$"

echo "== dxtechs_app (container api)"
expect_ok "đọc/ghi bảng tạo SAU roles.sql (default privileges)" dxtechs_app "$app_pw" \
  "INSERT INTO sandbox.products (name) VALUES ('Đồng hồ'); UPDATE sandbox.products SET name = 'Máy in'; SELECT * FROM sandbox.products; DELETE FROM sandbox.products"
expect_ok "đọc/ghi bảng có TRƯỚC roles.sql" dxtechs_app "$app_pw" \
  "INSERT INTO legacy.notes (body) VALUES ('x'); SELECT nextval('legacy.free_seq'); DELETE FROM legacy.notes"
expect_ok "đọc lịch sử migration" dxtechs_app "$app_pw" "SELECT count(*) FROM public.\"__EFMigrationsHistory\""
expect_ok "dùng unaccent" dxtechs_app "$app_pw" "SELECT unaccent('Điện thoại Việt Nam')"
expect_denied "ghi lịch sử migration" dxtechs_app "$app_pw" \
  "INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('x', 'y')"
expect_denied "tạo bảng trong public" dxtechs_app "$app_pw" "CREATE TABLE public.hack (id int)"
expect_denied "tạo bảng trong schema module" dxtechs_app "$app_pw" "CREATE TABLE sandbox.hack (id int)"
expect_denied "tạo schema" dxtechs_app "$app_pw" "CREATE SCHEMA hack"
expect_denied "xoá bảng" dxtechs_app "$app_pw" "DROP TABLE sandbox.products"
expect_denied "sửa cấu trúc bảng" dxtechs_app "$app_pw" "ALTER TABLE sandbox.products ADD COLUMN x int"
expect_denied "TRUNCATE" dxtechs_app "$app_pw" "TRUNCATE sandbox.products"

echo "== Đổi mật khẩu bằng cách chạy lại với giá trị mới"
new_app_pw="$(openssl rand -hex 24)"
new_owner_pw="$owner_pw"
DB_APP_PASSWORD="$new_app_pw" run_roles
expect_ok "đăng nhập bằng mật khẩu mới" dxtechs_app "$new_app_pw" "SELECT 1"
expect_denied "mật khẩu cũ hết hiệu lực" dxtechs_app "$app_pw" "SELECT 1"

# Migration EF thật chạy bằng dxtechs_owner — đúng con đường của container "migrate" trên production.
# Bật bằng DB_ROLES_RUN_MIGRATIONS=1 (CI); cần đã build server/server.csproj cấu hình Release.
if [ "${DB_ROLES_RUN_MIGRATIONS:-0}" = 1 ]; then
  echo "== Migration EF bằng dxtechs_owner, rồi dxtechs_app dùng bảng mới"
  owner_conn="Host=${PGHOST:-localhost};Port=${PGPORT:-5432};Database=$db;Username=dxtechs_owner;Password=$new_owner_pw"
  if (cd "$repo_root" && dotnet tool restore >/dev/null \
        && ASPNETCORE_ENVIRONMENT=Development dotnet tool run dotnet-ef database update \
             --project server/server.csproj --configuration Release --no-build --connection "$owner_conn" >/dev/null); then
    pass "dotnet ef database update bằng dxtechs_owner"
  else
    fail "dotnet ef database update bằng dxtechs_owner"
  fi
  expect_value "schema catalog thuộc dxtechs_owner" "dxtechs_owner" \
    "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'catalog'"
  expect_ok "app đọc danh mục gốc do migration chèn" dxtechs_app "$new_app_pw" \
    "SELECT 1 / (count(*) = 6)::int FROM catalog.categories WHERE parent_id IS NULL AND slug IN ('software','cloud','domain','ssl','storage','backup')"
  expect_ok "app ghi sản phẩm + tìm không dấu" dxtechs_app "$new_app_pw" \
    "INSERT INTO catalog.products (id, category_id, slug, name, license_type) SELECT gen_random_uuid(), id, 'roles-test', 'Phần mềm Đồ hoạ', 'perpetual' FROM catalog.categories WHERE slug = 'software';
     SELECT 1 / count(*)::int FROM catalog.products WHERE search_vector @@ to_tsquery('simple', catalog.f_unaccent('phan & mem & do & hoa'));
     DELETE FROM catalog.products WHERE slug = 'roles-test'"
  # PR-7 (ADR-012): schema identity + citext do roles.sql (superuser) tạo trước migration.
  expect_value "schema identity thuộc dxtechs_owner" "dxtechs_owner" \
    "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'identity'"
  expect_ok "app tạo user; email trùng khác hoa thường bị UNIQUE (citext) chặn" dxtechs_app "$new_app_pw" \
    "INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp)
       VALUES (gen_random_uuid(), 'Roles.Test@DXTechs.com', repeat('h', 40), 'Roles Test', 'customer', 'active', 0, gen_random_uuid());
     DO \$\$ BEGIN
       INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp)
         VALUES (gen_random_uuid(), 'roles.test@dxtechs.com', repeat('h', 40), 'Roles Test', 'customer', 'active', 0, gen_random_uuid());
       RAISE EXCEPTION 'citext không chặn email trùng';
     EXCEPTION WHEN unique_violation THEN NULL; END \$\$;
     DELETE FROM identity.users WHERE email = 'roles.test@dxtechs.com'"
  # PR-9 (thiết kế mục A5): chứng từ Phase 3 không bị xoá; lịch sử + payment chỉ thêm; payment chỉ sửa cột xử lý lệch.
  phase3_seed="
    INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp)
      VALUES ('00000000-0000-7000-8000-000000000001', 'p3@dxtechs.test', repeat('h', 40), 'P3', 'customer', 'active', 0, gen_random_uuid())
      ON CONFLICT DO NOTHING;
    INSERT INTO sales.orders (id, code, user_id, idempotency_key, status, customer_email, customer_name, subtotal_vnd, discount_vnd,
                              total_vnd, expires_at, created_at, updated_at)
      VALUES ('00000000-0000-7000-8000-000000000002', 'DX7K3QH9M2', '00000000-0000-7000-8000-000000000001', gen_random_uuid(),
              'pending_payment', 'p3@dxtechs.test', 'P3', 1000, 0, 1000, now() + interval '30 minutes', now(), now())
      ON CONFLICT DO NOTHING;
    -- Đơn KHÔNG có dòng phụ thuộc: DELETE chỉ có thể bị chặn bởi quyền, không phải bởi khoá ngoại.
    INSERT INTO sales.orders (id, code, user_id, idempotency_key, status, customer_email, customer_name, subtotal_vnd, discount_vnd,
                              total_vnd, expires_at, created_at, updated_at)
      VALUES ('00000000-0000-7000-8000-000000000004', 'DX7K3QH9M3', '00000000-0000-7000-8000-000000000001', gen_random_uuid(),
              'pending_payment', 'p3@dxtechs.test', 'P3', 1000, 0, 1000, now() + interval '30 minutes', now(), now())
      ON CONFLICT DO NOTHING;
    INSERT INTO sales.order_status_history (order_id, to_status, actor_type) VALUES ('00000000-0000-7000-8000-000000000002', 'pending_payment', 'customer');
    INSERT INTO payments.payments (id, order_id, provider, provider_txn_id, transfer_content, amount_vnd, match_status, paid_at)
      VALUES ('00000000-0000-7000-8000-000000000003', '00000000-0000-7000-8000-000000000002', 'sepay', 'roles-test-1', 'DX7K3QH9M2', 1000, 'matched', now())
      ON CONFLICT DO NOTHING"
  expect_ok "app tạo đơn + lịch sử + payment (Phase 3)" dxtechs_app "$new_app_pw" "$phase3_seed"
  phase3_checks() {
    expect_denied "app xoá đơn hàng ($1)" dxtechs_app "$new_app_pw" "DELETE FROM sales.orders WHERE code = 'DX7K3QH9M3'"
    expect_denied "app xoá license key ($1)" dxtechs_app "$new_app_pw" "DELETE FROM inventory.license_keys"
    expect_denied "app sửa lịch sử trạng thái ($1)" dxtechs_app "$new_app_pw" "UPDATE sales.order_status_history SET reason = 'x'"
    expect_denied "app xoá lịch sử trạng thái ($1)" dxtechs_app "$new_app_pw" "DELETE FROM sales.order_status_history"
    expect_denied "app sửa số tiền payment ($1)" dxtechs_app "$new_app_pw" "UPDATE payments.payments SET amount_vnd = 1 WHERE provider_txn_id = 'roles-test-1'"
    expect_denied "app xoá payment ($1)" dxtechs_app "$new_app_pw" "DELETE FROM payments.payments"
    expect_ok "app ghi kết quả xử lý khoản lệch ($1)" dxtechs_app "$new_app_pw" \
      "UPDATE payments.payments SET resolution_note = 'kiểm tra quyền' WHERE provider_txn_id = 'roles-test-1'"
    expect_ok "app cập nhật trạng thái đơn ($1)" dxtechs_app "$new_app_pw" \
      "UPDATE sales.orders SET note = 'kiểm tra quyền' WHERE code = 'DX7K3QH9M2'"
    expect_ok "app dọn webhook cũ ($1)" dxtechs_app "$new_app_pw" "DELETE FROM payments.webhook_events WHERE received_at < now() - interval '180 days'"
  }
  # CT-1 (ADR-015): role editor hợp lệ; bài/chủ đề/lead không xoá cứng; revision chỉ thêm (được xoá để dọn bản cũ).
  content_seed="
    INSERT INTO identity.users (id, email, password_hash, full_name, role, status, failed_login_count, security_stamp)
      VALUES ('00000000-0000-7000-8000-000000000011', 'editor@dxtechs.test', repeat('h', 40), 'Editor', 'editor', 'active', 0, gen_random_uuid())
      ON CONFLICT DO NOTHING;
    INSERT INTO content.topics (id, post_type, slug, name, sort_order) VALUES ('00000000-0000-7000-8000-000000000012', 'news', 'roles-test', 'Roles', 0)
      ON CONFLICT DO NOTHING;
    INSERT INTO content.posts (id, post_type, topic_id, slug, title, blocks, status, published_at, author_id, updated_by, sort_order, is_featured)
      VALUES ('00000000-0000-7000-8000-000000000013', 'news', '00000000-0000-7000-8000-000000000012', 'roles-test', 'Điện toán đám mây',
              '[{\"id\":\"b1\",\"type\":\"paragraph\",\"spans\":[{\"text\":\"Kiểm tra quyền\"}]}]', 'published', now(),
              '00000000-0000-7000-8000-000000000011', '00000000-0000-7000-8000-000000000011', 0, false)
      ON CONFLICT DO NOTHING;
    -- Bài + chủ đề KHÔNG có dòng phụ thuộc: DELETE chỉ có thể bị chặn bởi quyền, không phải bởi khoá ngoại.
    INSERT INTO content.topics (id, post_type, slug, name, sort_order) VALUES ('00000000-0000-7000-8000-000000000015', 'news', 'roles-empty', 'Rỗng', 0)
      ON CONFLICT DO NOTHING;
    INSERT INTO content.posts (id, post_type, slug, title, blocks, status, author_id, updated_by, sort_order, is_featured)
      VALUES ('00000000-0000-7000-8000-000000000016', 'news', 'roles-empty', 'Rỗng', '[]', 'draft',
              '00000000-0000-7000-8000-000000000011', '00000000-0000-7000-8000-000000000011', 0, false)
      ON CONFLICT DO NOTHING;
    INSERT INTO content.post_revisions (post_id, title, blocks, key_points, saved_by)
      VALUES ('00000000-0000-7000-8000-000000000013', 'v1', '[]', '[]', '00000000-0000-7000-8000-000000000011');
    INSERT INTO content.leads (id, kind, full_name, email, consent_version, consent_at)
      VALUES ('00000000-0000-7000-8000-000000000014', 'consultation', 'Khách', 'lead@dxtechs.test', 'v1', now()) ON CONFLICT DO NOTHING"
  expect_ok "app tạo bài + revision + lead, user role editor (Content)" dxtechs_app "$new_app_pw" "$content_seed"
  content_checks() {
    expect_denied "app xoá bài viết ($1)" dxtechs_app "$new_app_pw" "DELETE FROM content.posts WHERE slug = 'roles-empty'"
    expect_denied "app xoá chủ đề ($1)" dxtechs_app "$new_app_pw" "DELETE FROM content.topics WHERE slug = 'roles-empty'"
    expect_denied "app xoá lead ($1)" dxtechs_app "$new_app_pw" "DELETE FROM content.leads"
    expect_denied "app sửa revision ($1)" dxtechs_app "$new_app_pw" "UPDATE content.post_revisions SET title = 'x'"
    expect_ok "app sửa bài + tìm không dấu trong khối ($1)" dxtechs_app "$new_app_pw" \
      "UPDATE content.posts SET excerpt = 'x' WHERE slug = 'roles-test';
       DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM content.posts WHERE search_vector @@ plainto_tsquery('simple', 'kiem tra quyen'))
         THEN RAISE EXCEPTION 'search_vector không chứa chữ trong khối'; END IF; END \$\$"
    expect_ok "app ẩn danh hoá lead ($1)" dxtechs_app "$new_app_pw" \
      "UPDATE content.leads SET full_name = 'Đã ẩn danh', email = NULL, anonymized_at = now() WHERE id = '00000000-0000-7000-8000-000000000014'"
    expect_ok "app dọn revision cũ ($1)" dxtechs_app "$new_app_pw" "DELETE FROM content.post_revisions WHERE title = 'không tồn tại'"
  }
  content_checks "sau migration"
  phase3_checks "sau migration"
  # db-roles chạy lại ở MỌI lần deploy và mục 5 cấp lại toàn bộ quyền ⇒ mục 5b phải thu hồi lại.
  DB_APP_PASSWORD="$new_app_pw" run_roles
  phase3_checks "sau khi chạy lại roles.sql"
  content_checks "sau khi chạy lại roles.sql"
  expect_denied "app xoá bảng identity" dxtechs_app "$new_app_pw" "DROP TABLE identity.refresh_tokens"
  expect_denied "app tạo bảng trong catalog" dxtechs_app "$new_app_pw" "CREATE TABLE catalog.hack (id int)"
  expect_denied "app xoá bảng catalog" dxtechs_app "$new_app_pw" "DROP TABLE catalog.product_variants"
  expect_denied "app ghi lịch sử migration" dxtechs_app "$new_app_pw" \
    "INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('x2', 'y')"
fi

if [ "$failures" -ne 0 ]; then
  echo "::error::roles.sql: $failures kiểm tra thất bại"
  exit 1
fi
echo "roles.sql: tất cả kiểm tra đạt"
