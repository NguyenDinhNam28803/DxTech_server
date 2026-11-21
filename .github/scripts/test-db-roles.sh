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
