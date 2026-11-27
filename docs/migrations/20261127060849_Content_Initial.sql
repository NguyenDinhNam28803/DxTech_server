-- Sinh bởi: dotnet tool run dotnet-ef migrations script Phase3_Sales_Payments_Inventory Content_Initial --project server/server.csproj
-- Chỉ để review (CT-1, ADR-015, thiết kế docs/design/ct-content-module.md mục 1). Production áp qua container "migrate" (dxtechs_owner).
-- Khối DO $content$ cuối file: thu hồi quyền dxtechs_app; roles.sql mục 5c lặp lại mỗi lần deploy.
Build started...
Build succeeded.
START TRANSACTION;

ALTER TABLE identity.users DROP CONSTRAINT ck_users_role;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'content') THEN
        CREATE SCHEMA content;
    END IF;
END $EF$;

CREATE FUNCTION content.f_unaccent(input text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    RETURN public.unaccent('public.unaccent'::regdictionary, input);

-- Chữ trong khối (text của đoạn/tiêu đề, câu hỏi FAQ, nhãn CTA, chú thích ảnh, trích dẫn) cho cột tìm kiếm.
-- jsonb_path_query (không _tz) là IMMUTABLE ⇒ dùng được trong cột generated.
CREATE FUNCTION content.f_blocks_text(blocks jsonb) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    RETURN (
        SELECT coalesce(string_agg(v #>> '{}', ' '), '')
          FROM unnest(ARRAY['strict $.**.text', 'strict $.**.question', 'strict $.**.label',
                            'strict $.**.caption', 'strict $.**.cite']::jsonpath[]) AS p(path),
               jsonb_path_query(blocks, p.path) AS v
         WHERE jsonb_typeof(v) = 'string');

CREATE TABLE content.media (
    id uuid NOT NULL,
    file_name text NOT NULL,
    content_type text NOT NULL,
    bytes bigint NOT NULL,
    width integer NOT NULL,
    height integer NOT NULL,
    alt_default text,
    sha256 bytea NOT NULL,
    uploaded_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    CONSTRAINT pk_media PRIMARY KEY (id),
    CONSTRAINT ck_media_alt_default CHECK (alt_default IS NULL OR char_length(alt_default) <= 200),
    CONSTRAINT ck_media_bytes CHECK (bytes BETWEEN 1 AND 5242880),
    CONSTRAINT ck_media_content_type CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT ck_media_dimensions CHECK (width BETWEEN 1 AND 6000 AND height BETWEEN 1 AND 6000),
    CONSTRAINT ck_media_file_name CHECK (file_name ~ '^[0-9a-f]{32}\.(jpg|png|webp)$'),
    CONSTRAINT ck_media_sha256 CHECK (octet_length(sha256) = 32),
    CONSTRAINT fk_media_users_uploaded_by FOREIGN KEY (uploaded_by) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE content.topics (
    id uuid NOT NULL,
    post_type text NOT NULL,
    parent_id uuid,
    slug text NOT NULL,
    name text NOT NULL,
    description text,
    image_url text,
    sort_order integer NOT NULL,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    CONSTRAINT pk_topics PRIMARY KEY (id),
    CONSTRAINT ck_topics_description CHECK (description IS NULL OR char_length(description) <= 500),
    CONSTRAINT ck_topics_image_url CHECK (image_url IS NULL OR (image_url ~ '^https://[^\s]+$' AND char_length(image_url) <= 2000)),
    CONSTRAINT ck_topics_name CHECK (char_length(name) BETWEEN 1 AND 100),
    CONSTRAINT ck_topics_not_own_parent CHECK (parent_id IS DISTINCT FROM id),
    CONSTRAINT ck_topics_post_type CHECK (post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')),
    CONSTRAINT ck_topics_slug CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) <= 150),
    CONSTRAINT fk_topics_topics_parent_id FOREIGN KEY (parent_id) REFERENCES content.topics (id) ON DELETE RESTRICT
);

CREATE TABLE content.posts (
    id uuid NOT NULL,
    post_type text NOT NULL,
    topic_id uuid,
    parent_id uuid,
    slug text NOT NULL,
    title text NOT NULL,
    excerpt text,
    label text,
    blocks jsonb NOT NULL DEFAULT ('[]'::jsonb),
    key_points jsonb NOT NULL DEFAULT ('[]'::jsonb),
    cover_media_id uuid,
    cover_image_url text,
    cover_image_alt text,
    status text NOT NULL DEFAULT 'draft',
    published_at timestamp with time zone,
    author_id uuid NOT NULL,
    updated_by uuid NOT NULL,
    seo_title text,
    seo_description text,
    reading_minutes smallint,
    sort_order integer NOT NULL,
    is_featured boolean NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    search_vector tsvector GENERATED ALWAYS AS (setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(title, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(excerpt, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(content.f_blocks_text(blocks))), 'C')) STORED,
    CONSTRAINT pk_posts PRIMARY KEY (id),
    CONSTRAINT ck_posts_blocks CHECK (jsonb_typeof(blocks) = 'array' AND jsonb_array_length(blocks) <= 200),
    CONSTRAINT ck_posts_cover_image CHECK ((cover_image_url IS NULL OR (cover_image_url ~ '^https://[^\s]+$' AND char_length(cover_image_url) <= 2000)) AND (cover_image_alt IS NULL OR char_length(cover_image_alt) <= 200)),
    CONSTRAINT ck_posts_excerpt CHECK (excerpt IS NULL OR char_length(excerpt) <= 500),
    CONSTRAINT ck_posts_key_points CHECK (jsonb_typeof(key_points) = 'array' AND jsonb_array_length(key_points) <= 10),
    CONSTRAINT ck_posts_label CHECK (label IS NULL OR char_length(label) <= 40),
    CONSTRAINT ck_posts_not_own_parent CHECK (parent_id IS DISTINCT FROM id),
    CONSTRAINT ck_posts_post_type CHECK (post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')),
    CONSTRAINT ck_posts_published_at CHECK (status <> 'published' OR published_at IS NOT NULL),
    CONSTRAINT ck_posts_reading_minutes CHECK (reading_minutes IS NULL OR reading_minutes BETWEEN 1 AND 600),
    CONSTRAINT ck_posts_seo CHECK ((seo_title IS NULL OR char_length(seo_title) <= 70) AND (seo_description IS NULL OR char_length(seo_description) <= 160)),
    CONSTRAINT ck_posts_slug CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) <= 150),
    CONSTRAINT ck_posts_status CHECK (status IN ('draft', 'published', 'archived')),
    CONSTRAINT ck_posts_title CHECK (char_length(title) BETWEEN 1 AND 200),
    CONSTRAINT fk_posts_media_cover_media_id FOREIGN KEY (cover_media_id) REFERENCES content.media (id) ON DELETE RESTRICT,
    CONSTRAINT fk_posts_posts_parent_id FOREIGN KEY (parent_id) REFERENCES content.posts (id) ON DELETE RESTRICT,
    CONSTRAINT fk_posts_topics_topic_id FOREIGN KEY (topic_id) REFERENCES content.topics (id) ON DELETE RESTRICT,
    CONSTRAINT fk_posts_users_author_id FOREIGN KEY (author_id) REFERENCES identity.users (id) ON DELETE RESTRICT,
    CONSTRAINT fk_posts_users_updated_by FOREIGN KEY (updated_by) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE content.course_sessions (
    id uuid NOT NULL,
    post_id uuid NOT NULL,
    starts_on date NOT NULL,
    ends_on date,
    format text NOT NULL,
    location text,
    seats integer,
    price_vnd bigint,
    status text NOT NULL DEFAULT 'open',
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_course_sessions PRIMARY KEY (id),
    CONSTRAINT ck_course_sessions_dates CHECK (ends_on IS NULL OR ends_on >= starts_on),
    CONSTRAINT ck_course_sessions_format CHECK (format IN ('online', 'offline', 'hybrid')),
    CONSTRAINT ck_course_sessions_location CHECK (location IS NULL OR char_length(location) <= 200),
    CONSTRAINT ck_course_sessions_price CHECK (price_vnd IS NULL OR price_vnd >= 0),
    CONSTRAINT ck_course_sessions_seats CHECK (seats IS NULL OR seats > 0),
    CONSTRAINT ck_course_sessions_status CHECK (status IN ('open', 'full', 'closed')),
    CONSTRAINT fk_course_sessions_posts_post_id FOREIGN KEY (post_id) REFERENCES content.posts (id) ON DELETE RESTRICT
);

CREATE TABLE content.post_revisions (
    id bigint GENERATED ALWAYS AS IDENTITY,
    post_id uuid NOT NULL,
    title text NOT NULL,
    blocks jsonb NOT NULL,
    key_points jsonb NOT NULL,
    saved_by uuid NOT NULL,
    saved_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_post_revisions PRIMARY KEY (id),
    CONSTRAINT ck_post_revisions_blocks CHECK (jsonb_typeof(blocks) = 'array'),
    CONSTRAINT ck_post_revisions_key_points CHECK (jsonb_typeof(key_points) = 'array'),
    CONSTRAINT ck_post_revisions_title CHECK (char_length(title) BETWEEN 1 AND 200),
    CONSTRAINT fk_post_revisions_posts_post_id FOREIGN KEY (post_id) REFERENCES content.posts (id) ON DELETE RESTRICT,
    CONSTRAINT fk_post_revisions_users_saved_by FOREIGN KEY (saved_by) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE content.slug_redirects (
    id bigint GENERATED ALWAYS AS IDENTITY,
    post_type text NOT NULL,
    old_slug text NOT NULL,
    post_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_slug_redirects PRIMARY KEY (id),
    CONSTRAINT ck_slug_redirects_old_slug CHECK (old_slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(old_slug) <= 150),
    CONSTRAINT ck_slug_redirects_post_type CHECK (post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')),
    CONSTRAINT fk_slug_redirects_posts_post_id FOREIGN KEY (post_id) REFERENCES content.posts (id) ON DELETE CASCADE
);

CREATE TABLE content.leads (
    id uuid NOT NULL,
    kind text NOT NULL,
    post_id uuid,
    session_id uuid,
    full_name text NOT NULL,
    email citext,
    phone text,
    company text,
    message text,
    consent_version text NOT NULL,
    consent_at timestamp with time zone NOT NULL,
    source_ip inet,
    user_agent text,
    status text NOT NULL DEFAULT 'new',
    handled_by uuid,
    note text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    anonymized_at timestamp with time zone,
    CONSTRAINT pk_leads PRIMARY KEY (id),
    CONSTRAINT ck_leads_consent_version CHECK (char_length(consent_version) BETWEEN 1 AND 40),
    CONSTRAINT ck_leads_contact CHECK (anonymized_at IS NOT NULL OR email IS NOT NULL OR phone IS NOT NULL),
    CONSTRAINT ck_leads_course CHECK (kind <> 'course' OR post_id IS NOT NULL),
    CONSTRAINT ck_leads_email CHECK (email IS NULL OR char_length(email) BETWEEN 3 AND 254),
    CONSTRAINT ck_leads_full_name CHECK (char_length(full_name) BETWEEN 1 AND 100),
    CONSTRAINT ck_leads_kind CHECK (kind IN ('consultation', 'course')),
    CONSTRAINT ck_leads_phone CHECK (phone IS NULL OR phone ~ '^\+?[0-9]{8,15}$'),
    CONSTRAINT ck_leads_status CHECK (status IN ('new', 'contacted', 'closed')),
    CONSTRAINT ck_leads_texts CHECK ((company IS NULL OR char_length(company) <= 200) AND (message IS NULL OR char_length(message) <= 2000) AND (user_agent IS NULL OR char_length(user_agent) <= 500) AND (note IS NULL OR char_length(note) <= 2000)),
    CONSTRAINT fk_leads_course_sessions_session_id FOREIGN KEY (session_id) REFERENCES content.course_sessions (id) ON DELETE RESTRICT,
    CONSTRAINT fk_leads_posts_post_id FOREIGN KEY (post_id) REFERENCES content.posts (id) ON DELETE RESTRICT,
    CONSTRAINT fk_leads_users_handled_by FOREIGN KEY (handled_by) REFERENCES identity.users (id) ON DELETE RESTRICT
);

ALTER TABLE identity.users ADD CONSTRAINT ck_users_role CHECK (role IN ('customer', 'editor', 'admin'));

CREATE INDEX ix_course_sessions_post_id ON content.course_sessions (post_id, starts_on);

CREATE INDEX ix_leads_handled_by ON content.leads (handled_by);

CREATE INDEX ix_leads_post_id ON content.leads (post_id);

CREATE INDEX ix_leads_session_id ON content.leads (session_id);

CREATE INDEX ix_leads_status_created_at ON content.leads (status, created_at DESC);

CREATE INDEX ix_leads_to_anonymize ON content.leads (created_at) WHERE anonymized_at IS NULL;

CREATE UNIQUE INDEX ix_media_file_name ON content.media (file_name);

CREATE UNIQUE INDEX ix_media_sha256 ON content.media (sha256);

CREATE INDEX ix_media_uploaded_by ON content.media (uploaded_by);

CREATE INDEX ix_post_revisions_post_id ON content.post_revisions (post_id, id DESC);

CREATE INDEX ix_post_revisions_saved_by ON content.post_revisions (saved_by);

CREATE INDEX ix_posts_admin_list ON content.posts (post_type, updated_at DESC);

CREATE INDEX ix_posts_author_id ON content.posts (author_id);

CREATE INDEX ix_posts_cover_media_id ON content.posts (cover_media_id);

CREATE INDEX ix_posts_parent_id_sort_order ON content.posts (parent_id, sort_order);

CREATE INDEX ix_posts_public_list ON content.posts (post_type, published_at DESC) WHERE status = 'published' AND deleted_at IS NULL;

CREATE INDEX ix_posts_search_vector ON content.posts USING gin (search_vector);

CREATE INDEX ix_posts_topic_id_sort_order ON content.posts (topic_id, sort_order);

CREATE INDEX ix_posts_updated_by ON content.posts (updated_by);

CREATE UNIQUE INDEX ux_posts_type_slug ON content.posts (post_type, slug) WHERE deleted_at IS NULL;

CREATE INDEX ix_slug_redirects_post_id ON content.slug_redirects (post_id);

CREATE UNIQUE INDEX ux_slug_redirects_type_old_slug ON content.slug_redirects (post_type, old_slug);

CREATE INDEX ix_topics_parent_id ON content.topics (parent_id);

CREATE UNIQUE INDEX ux_topics_type_parent_slug ON content.topics (post_type, parent_id, slug) NULLS NOT DISTINCT WHERE deleted_at IS NULL;

DO $content$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_app') THEN
    REVOKE DELETE, TRUNCATE ON content.posts, content.topics, content.media, content.leads FROM dxtechs_app;
    REVOKE UPDATE, TRUNCATE ON content.post_revisions FROM dxtechs_app;
    REVOKE TRUNCATE ON content.slug_redirects, content.course_sessions FROM dxtechs_app;
  END IF;
END
$content$;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261127060849_Content_Initial', '8.0.8');

COMMIT;


