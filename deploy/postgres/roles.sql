-- ADR-009: tách quyền database thành 2 role.
--
--   dxtechs_owner : sở hữu database, schema, bảng. Chỉ container `migrate` dùng (chạy EF migrations).
--   dxtechs_app   : chỉ SELECT/INSERT/UPDATE/DELETE. Container `api` dùng.
--                   Không tạo/xoá/sửa cấu trúc bảng, không ghi lịch sử migration.
--
-- Chạy bằng SUPERUSER (POSTGRES_USER), qua service `db-roles` trong docker-compose.prod.yml,
-- TRƯỚC `migrate` ở MỌI lần deploy. Script idempotent: chạy lại bao nhiêu lần cũng ra cùng trạng thái.
--   - Đổi mật khẩu role = sửa DB_OWNER_PASSWORD / DB_APP_PASSWORD trong .env rồi deploy lại.
--   - Database mới (volume rỗng) hay database đang chạy đều dùng chung script này.
--
-- Mật khẩu đọc từ biến môi trường (\getenv, cần psql >= 15), không truyền qua dòng lệnh.
-- Chạy tay (ví dụ trên VPS, từ ~/dxtechs-server):
--   docker compose ... run --rm db-roles

\set ON_ERROR_STOP on
-- terse: thông báo lỗi không in lại câu lệnh (tránh lộ mật khẩu trong log CI).
\set VERBOSITY terse
\set QUIET on
SET client_min_messages = warning;

\getenv owner_password DB_OWNER_PASSWORD
\getenv app_password DB_APP_PASSWORD

-- 1. Kiểm tra đầu vào --------------------------------------------------------------------------
\if :{?owner_password}
\else
  DO $$ BEGIN RAISE EXCEPTION 'roles.sql: thiếu biến môi trường DB_OWNER_PASSWORD'; END $$;
\endif
\if :{?app_password}
\else
  DO $$ BEGIN RAISE EXCEPTION 'roles.sql: thiếu biến môi trường DB_APP_PASSWORD'; END $$;
\endif

SELECT length(:'owner_password') >= 32
   AND length(:'app_password') >= 32
   AND :'owner_password' <> :'app_password' AS passwords_ok
\gset
\if :passwords_ok
\else
  DO $$ BEGIN RAISE EXCEPTION 'roles.sql: DB_OWNER_PASSWORD và DB_APP_PASSWORD phải dài >= 32 ký tự và khác nhau (dùng: openssl rand -hex 24)'; END $$;
\endif

SELECT rolsuper AS is_superuser FROM pg_roles WHERE rolname = current_user \gset
\if :is_superuser
\else
  DO $$ BEGIN RAISE EXCEPTION 'roles.sql: phải chạy bằng superuser (POSTGRES_USER)'; END $$;
\endif

BEGIN;

-- 2. Role -----------------------------------------------------------------------------------------
SELECT 'CREATE ROLE dxtechs_owner'
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_owner') \gexec
SELECT 'CREATE ROLE dxtechs_app'
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_app') \gexec

-- Luôn đặt lại thuộc tính + mật khẩu ⇒ .env là nguồn sự thật duy nhất.
ALTER ROLE dxtechs_owner WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
  PASSWORD :'owner_password';
ALTER ROLE dxtechs_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT
  PASSWORD :'app_password';

-- 3. Database -------------------------------------------------------------------------------------
-- Chủ database = dxtechs_owner. Từ PG 15, schema public thuộc pg_database_owner nên chuyển theo.
SELECT format('ALTER DATABASE %I OWNER TO dxtechs_owner', current_database()) \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database()) \gexec
SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO dxtechs_app', current_database()) \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- unaccent: tìm kiếm tiếng Việt không dấu (ADR-008, dùng từ PR-3). Tạo bằng superuser.
CREATE EXTENSION IF NOT EXISTS unaccent SCHEMA public;

-- 4. Chuyển quyền sở hữu đối tượng có sẵn (ví dụ __EFMigrationsHistory do superuser tạo) --------
-- Bỏ qua: schema hệ thống, schema public (đã thuộc pg_database_owner), đối tượng của extension,
-- sequence gắn với cột (serial/identity) — chúng tự đổi chủ theo bảng.
DO $$
DECLARE
  r record;
BEGIN
  FOR r IN
    SELECT n.nspname
      FROM pg_namespace n
     WHERE n.nspname NOT IN ('information_schema', 'public')
       AND n.nspname NOT LIKE 'pg\_%'
       AND n.nspowner <> 'dxtechs_owner'::regrole
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_namespace'::regclass AND d.objid = n.oid AND d.deptype = 'e')
  LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO dxtechs_owner', r.nspname);
  END LOOP;

  FOR r IN
    SELECT c.oid::regclass AS obj,
           CASE c.relkind WHEN 'S' THEN 'SEQUENCE' WHEN 'v' THEN 'VIEW'
                          WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE'
                          ELSE 'TABLE' END AS kind
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname <> 'information_schema'
       AND n.nspname NOT LIKE 'pg\_%'
       AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
       AND c.relowner <> 'dxtechs_owner'::regrole
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT (c.relkind = 'S' AND EXISTS (
                 SELECT 1 FROM pg_depend d
                  WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
                    AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')))
  LOOP
    EXECUTE format('ALTER %s %s OWNER TO dxtechs_owner', r.kind, r.obj);
  END LOOP;
END
$$;

-- 5. Quyền của dxtechs_app trên đối tượng ĐANG CÓ ------------------------------------------------
DO $$
DECLARE
  s text;
BEGIN
  FOR s IN
    SELECT n.nspname
      FROM pg_namespace n
     WHERE n.nspname <> 'information_schema'
       AND n.nspname NOT LIKE 'pg\_%'
  LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO dxtechs_app', s);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO dxtechs_app', s);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO dxtechs_app', s);
  END LOOP;
END
$$;

-- Lịch sử migration: API chỉ được đọc, không được ghi.
SELECT 'REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public."__EFMigrationsHistory" FROM dxtechs_app'
 WHERE to_regclass('public."__EFMigrationsHistory"') IS NOT NULL \gexec

-- 6. Quyền tự động cho đối tượng dxtechs_owner tạo SAU NÀY (schema/bảng của migration mới) -------
ALTER DEFAULT PRIVILEGES FOR ROLE dxtechs_owner GRANT USAGE ON SCHEMAS TO dxtechs_app;
ALTER DEFAULT PRIVILEGES FOR ROLE dxtechs_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dxtechs_app;
ALTER DEFAULT PRIVILEGES FOR ROLE dxtechs_owner GRANT USAGE, SELECT ON SEQUENCES TO dxtechs_app;

COMMIT;

\unset owner_password
\unset app_password
\set QUIET off
\echo 'roles.sql: OK (dxtechs_owner, dxtechs_app)'
