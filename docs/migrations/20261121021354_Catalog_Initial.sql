-- Sinh bởi: dotnet tool run dotnet-ef migrations script 0 Catalog_Initial --project server/server.csproj
-- Chỉ để review (quy ước migration mục 12 tài liệu thiết kế). Production áp migration qua container "migrate"
-- bằng role dxtechs_owner; extension unaccent đã do deploy/postgres/roles.sql (superuser) tạo sẵn.

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'catalog') THEN
        CREATE SCHEMA catalog;
    END IF;
END $EF$;

CREATE EXTENSION IF NOT EXISTS unaccent;

CREATE FUNCTION catalog.f_unaccent(input text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    RETURN public.unaccent('public.unaccent'::regdictionary, input);

CREATE TABLE catalog.categories (
    id uuid NOT NULL,
    parent_id uuid,
    slug text NOT NULL,
    name text NOT NULL,
    description text,
    sort_order integer NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    CONSTRAINT pk_categories PRIMARY KEY (id),
    CONSTRAINT ck_categories_name CHECK (char_length(name) BETWEEN 1 AND 100),
    CONSTRAINT ck_categories_not_own_parent CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT ck_categories_slug CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT fk_categories_categories_parent_id FOREIGN KEY (parent_id) REFERENCES catalog.categories (id) ON DELETE RESTRICT
);

CREATE TABLE catalog.products (
    id uuid NOT NULL,
    category_id uuid NOT NULL,
    slug text NOT NULL,
    name text NOT NULL,
    short_description text,
    description text,
    brand text,
    license_type text NOT NULL,
    platforms text[] NOT NULL DEFAULT ('{}'::text[]),
    features jsonb NOT NULL DEFAULT ('[]'::jsonb),
    image_url text,
    badge text,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    search_vector tsvector GENERATED ALWAYS AS (setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(name, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(brand, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(short_description, ''))), 'C')) STORED,
    CONSTRAINT pk_products PRIMARY KEY (id),
    CONSTRAINT ck_products_features_array CHECK (jsonb_typeof(features) = 'array'),
    CONSTRAINT ck_products_license_type CHECK (license_type IN ('perpetual', 'subscription', 'service')),
    CONSTRAINT ck_products_name CHECK (char_length(name) BETWEEN 1 AND 200),
    CONSTRAINT ck_products_platforms CHECK (platforms <@ ARRAY['windows', 'macos', 'linux', 'ios', 'android', 'web']::text[]),
    CONSTRAINT ck_products_slug CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT fk_products_categories_category_id FOREIGN KEY (category_id) REFERENCES catalog.categories (id) ON DELETE RESTRICT
);

CREATE TABLE catalog.product_variants (
    id uuid NOT NULL,
    product_id uuid NOT NULL,
    sku text NOT NULL,
    name text NOT NULL,
    term_months integer,
    seats integer NOT NULL DEFAULT 1,
    price_vnd bigint NOT NULL,
    compare_at_price_vnd bigint,
    delivery_type text NOT NULL,
    is_default boolean NOT NULL,
    sort_order integer NOT NULL,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    CONSTRAINT pk_product_variants PRIMARY KEY (id),
    CONSTRAINT ck_product_variants_compare_at_price CHECK (compare_at_price_vnd IS NULL OR compare_at_price_vnd > price_vnd),
    CONSTRAINT ck_product_variants_delivery_type CHECK (delivery_type IN ('license_key', 'account', 'manual')),
    CONSTRAINT ck_product_variants_name CHECK (char_length(name) BETWEEN 1 AND 200),
    CONSTRAINT ck_product_variants_price CHECK (price_vnd >= 0),
    CONSTRAINT ck_product_variants_seats CHECK (seats > 0),
    CONSTRAINT ck_product_variants_sku CHECK (sku ~ '^[A-Za-z0-9][A-Za-z0-9._-]*$'),
    CONSTRAINT ck_product_variants_term_months CHECK (term_months IS NULL OR term_months > 0),
    CONSTRAINT fk_product_variants_products_product_id FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE RESTRICT
);

CREATE INDEX ix_categories_parent_id ON catalog.categories (parent_id);

CREATE UNIQUE INDEX ix_categories_slug ON catalog.categories (slug) WHERE deleted_at IS NULL;

CREATE INDEX ix_product_variants_product_id_price_vnd ON catalog.product_variants (product_id, price_vnd);

CREATE UNIQUE INDEX ix_product_variants_sku ON catalog.product_variants (sku) WHERE deleted_at IS NULL;

CREATE UNIQUE INDEX ux_product_variants_one_default ON catalog.product_variants (product_id) WHERE is_default AND deleted_at IS NULL;

CREATE INDEX ix_products_category_id_listed ON catalog.products (category_id) WHERE deleted_at IS NULL AND is_active;

CREATE INDEX ix_products_platforms ON catalog.products USING gin (platforms);

CREATE INDEX ix_products_search_vector ON catalog.products USING gin (search_vector);

CREATE UNIQUE INDEX ix_products_slug ON catalog.products (slug) WHERE deleted_at IS NULL;

INSERT INTO catalog.categories (id, slug, name, description, sort_order) VALUES
    ('01a1fbe1-6754-7541-941d-ea90e3341d07', 'software', 'Phần mềm bản quyền', 'Hệ điều hành, ứng dụng văn phòng, thiết kế và bảo mật', 10),
    ('01a1fbe1-6755-7910-ad9b-b6a54662e9e7', 'cloud', 'Cloud & Hosting', 'Máy chủ đám mây NVMe và hosting tốc độ cao LiteSpeed', 20),
    ('01a1fbe1-6756-7684-9793-dae8d847ae8a', 'domain', 'Tên miền', 'Đăng ký và bảo vệ thương hiệu trực tuyến toàn cầu', 30),
    ('01a1fbe1-6757-7db9-bfca-591a2b816890', 'ssl', 'SSL & Bảo mật', 'Chứng chỉ mã hóa bảo mật website và xác thực doanh nghiệp', 40),
    ('01a1fbe1-6758-778f-b61e-991c16fdca2c', 'storage', 'Lưu trữ đám mây', 'Bộ nhớ lưu trữ doanh nghiệp, đồng bộ tệp an toàn', 50),
    ('01a1fbe1-6759-7d85-b200-c5eebae07ed6', 'backup', 'Sao lưu & Phục hồi', 'Giải pháp sao lưu tự động và khôi phục dữ liệu thảm họa', 60);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261121021354_Catalog_Initial', '8.0.8');

COMMIT;

