-- Sinh bởi: dotnet tool run dotnet-ef migrations script Catalog_Initial Identity_Initial --project server/server.csproj
-- Chỉ để review (ADR-012, PR-7). Production áp migration qua container "migrate" bằng role dxtechs_owner;
-- extension citext đã do deploy/postgres/roles.sql (superuser) tạo sẵn ⇒ CREATE EXTENSION IF NOT EXISTS không cần quyền thêm.

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'identity') THEN
        CREATE SCHEMA identity;
    END IF;
END $EF$;

CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS unaccent;

CREATE TABLE identity.users (
    id uuid NOT NULL,
    email citext NOT NULL,
    email_confirmed_at timestamp with time zone,
    password_hash text NOT NULL,
    full_name text NOT NULL,
    phone text,
    company_name text,
    tax_code text,
    role text NOT NULL,
    status text NOT NULL,
    failed_login_count integer NOT NULL,
    lockout_until timestamp with time zone,
    security_stamp uuid NOT NULL,
    last_login_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT ck_users_company_name CHECK (company_name IS NULL OR char_length(company_name) BETWEEN 1 AND 200),
    CONSTRAINT ck_users_email CHECK (char_length(email) BETWEEN 3 AND 254 AND position('@' in email) > 1 AND email !~ '\s'),
    CONSTRAINT ck_users_failed_login_count CHECK (failed_login_count >= 0),
    CONSTRAINT ck_users_full_name CHECK (char_length(full_name) BETWEEN 1 AND 100),
    CONSTRAINT ck_users_password_hash CHECK (char_length(password_hash) BETWEEN 20 AND 500),
    CONSTRAINT ck_users_phone CHECK (phone IS NULL OR phone ~ '^\+?[0-9]{8,15}$'),
    CONSTRAINT ck_users_role CHECK (role IN ('customer', 'admin')),
    CONSTRAINT ck_users_status CHECK (status IN ('active', 'locked', 'disabled')),
    CONSTRAINT ck_users_tax_code CHECK (tax_code IS NULL OR tax_code ~ '^[0-9]{10}(-?[0-9]{3})?$')
);

CREATE TABLE identity.refresh_tokens (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    token_hash bytea NOT NULL,
    family_id uuid NOT NULL,
    replaced_by_id uuid,
    expires_at timestamp with time zone NOT NULL,
    session_expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    revoked_reason text,
    created_ip inet,
    user_agent text,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_refresh_tokens PRIMARY KEY (id),
    CONSTRAINT ck_refresh_tokens_expiry CHECK (expires_at > created_at AND expires_at <= session_expires_at),
    CONSTRAINT ck_refresh_tokens_hash_len CHECK (octet_length(token_hash) = 32),
    CONSTRAINT ck_refresh_tokens_not_own_replacement CHECK (replaced_by_id IS NULL OR replaced_by_id <> id),
    CONSTRAINT ck_refresh_tokens_revoked CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
    CONSTRAINT ck_refresh_tokens_revoked_reason CHECK (revoked_reason IS NULL OR revoked_reason IN ('rotated', 'logout', 'reuse_detected', 'password_changed', 'admin')),
    CONSTRAINT ck_refresh_tokens_user_agent CHECK (user_agent IS NULL OR char_length(user_agent) <= 512),
    CONSTRAINT fk_refresh_tokens_refresh_tokens_replaced_by_id FOREIGN KEY (replaced_by_id) REFERENCES identity.refresh_tokens (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refresh_tokens_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE CASCADE
);

CREATE INDEX ix_refresh_tokens_expires_at ON identity.refresh_tokens (expires_at);

CREATE INDEX ix_refresh_tokens_family_id ON identity.refresh_tokens (family_id);

CREATE INDEX ix_refresh_tokens_replaced_by_id ON identity.refresh_tokens (replaced_by_id);

CREATE INDEX ix_refresh_tokens_user_active ON identity.refresh_tokens (user_id) WHERE revoked_at IS NULL;

CREATE UNIQUE INDEX ux_refresh_tokens_token_hash ON identity.refresh_tokens (token_hash);

CREATE INDEX ix_users_created_at ON identity.users (created_at DESC, id DESC);

CREATE INDEX ix_users_role_admin ON identity.users (role) WHERE role = 'admin';

CREATE UNIQUE INDEX ux_users_email ON identity.users (email);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261124233715_Identity_Initial', '8.0.8');

COMMIT;

