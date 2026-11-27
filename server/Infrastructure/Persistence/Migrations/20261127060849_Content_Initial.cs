using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace server.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// CT-1 (ADR-015): schema <c>content</c> — 7 bảng, hàm IMMUTABLE <c>content.f_unaccent</c> + <c>content.f_blocks_text</c>
    /// cho cột generated <c>posts.search_vector</c>; role <c>editor</c> (CHECK <c>ck_users_role</c>); thu hồi quyền thừa của
    /// <c>dxtechs_app</c> (roles.sql mục 5c lặp lại mỗi lần deploy). Hàm chép nguyên văn từ <c>ContentSql.Functions</c> tại thời điểm tạo.
    /// </summary>
    public partial class Content_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_role",
                schema: "identity",
                table: "users");

            migrationBuilder.EnsureSchema(
                name: "content");

            // Phải có trước bảng posts (cột generated search_vector gọi hai hàm này).
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.CreateTable(
                name: "media",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    alt_default = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media", x => x.id);
                    table.CheckConstraint("ck_media_alt_default", "alt_default IS NULL OR char_length(alt_default) <= 200");
                    table.CheckConstraint("ck_media_bytes", "bytes BETWEEN 1 AND 5242880");
                    table.CheckConstraint("ck_media_content_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_media_dimensions", "width BETWEEN 1 AND 6000 AND height BETWEEN 1 AND 6000");
                    table.CheckConstraint("ck_media_file_name", "file_name ~ '^[0-9a-f]{32}\\.(jpg|png|webp)$'");
                    table.CheckConstraint("ck_media_sha256", "octet_length(sha256) = 32");
                    table.ForeignKey(
                        name: "fk_media_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "topics",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_type = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topics", x => x.id);
                    table.CheckConstraint("ck_topics_description", "description IS NULL OR char_length(description) <= 500");
                    table.CheckConstraint("ck_topics_image_url", "image_url IS NULL OR (image_url ~ '^https://[^\\s]+$' AND char_length(image_url) <= 2000)");
                    table.CheckConstraint("ck_topics_name", "char_length(name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_topics_not_own_parent", "parent_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_topics_post_type", "post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')");
                    table.CheckConstraint("ck_topics_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) <= 150");
                    table.ForeignKey(
                        name: "fk_topics_topics_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "content",
                        principalTable: "topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "posts",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_type = table.Column<string>(type: "text", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    excerpt = table.Column<string>(type: "text", nullable: true),
                    label = table.Column<string>(type: "text", nullable: true),
                    blocks = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    key_points = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    cover_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cover_image_url = table.Column<string>(type: "text", nullable: true),
                    cover_image_alt = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "draft"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    reading_minutes = table.Column<short>(type: "smallint", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_featured = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(title, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(excerpt, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(content.f_blocks_text(blocks))), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posts", x => x.id);
                    table.CheckConstraint("ck_posts_blocks", "jsonb_typeof(blocks) = 'array' AND jsonb_array_length(blocks) <= 200");
                    table.CheckConstraint("ck_posts_cover_image", "(cover_image_url IS NULL OR (cover_image_url ~ '^https://[^\\s]+$' AND char_length(cover_image_url) <= 2000)) AND (cover_image_alt IS NULL OR char_length(cover_image_alt) <= 200)");
                    table.CheckConstraint("ck_posts_excerpt", "excerpt IS NULL OR char_length(excerpt) <= 500");
                    table.CheckConstraint("ck_posts_key_points", "jsonb_typeof(key_points) = 'array' AND jsonb_array_length(key_points) <= 10");
                    table.CheckConstraint("ck_posts_label", "label IS NULL OR char_length(label) <= 40");
                    table.CheckConstraint("ck_posts_not_own_parent", "parent_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_posts_post_type", "post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')");
                    table.CheckConstraint("ck_posts_published_at", "status <> 'published' OR published_at IS NOT NULL");
                    table.CheckConstraint("ck_posts_reading_minutes", "reading_minutes IS NULL OR reading_minutes BETWEEN 1 AND 600");
                    table.CheckConstraint("ck_posts_seo", "(seo_title IS NULL OR char_length(seo_title) <= 70) AND (seo_description IS NULL OR char_length(seo_description) <= 160)");
                    table.CheckConstraint("ck_posts_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) <= 150");
                    table.CheckConstraint("ck_posts_status", "status IN ('draft', 'published', 'archived')");
                    table.CheckConstraint("ck_posts_title", "char_length(title) BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "fk_posts_media_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "content",
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_posts_posts_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "content",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_posts_topics_topic_id",
                        column: x => x.topic_id,
                        principalSchema: "content",
                        principalTable: "topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_posts_users_author_id",
                        column: x => x.author_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_posts_users_updated_by",
                        column: x => x.updated_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "course_sessions",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    format = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    seats = table.Column<int>(type: "integer", nullable: true),
                    price_vnd = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "open"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_sessions", x => x.id);
                    table.CheckConstraint("ck_course_sessions_dates", "ends_on IS NULL OR ends_on >= starts_on");
                    table.CheckConstraint("ck_course_sessions_format", "format IN ('online', 'offline', 'hybrid')");
                    table.CheckConstraint("ck_course_sessions_location", "location IS NULL OR char_length(location) <= 200");
                    table.CheckConstraint("ck_course_sessions_price", "price_vnd IS NULL OR price_vnd >= 0");
                    table.CheckConstraint("ck_course_sessions_seats", "seats IS NULL OR seats > 0");
                    table.CheckConstraint("ck_course_sessions_status", "status IN ('open', 'full', 'closed')");
                    table.ForeignKey(
                        name: "fk_course_sessions_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "content",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "post_revisions",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    blocks = table.Column<string>(type: "jsonb", nullable: false),
                    key_points = table.Column<string>(type: "jsonb", nullable: false),
                    saved_by = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_revisions", x => x.id);
                    table.CheckConstraint("ck_post_revisions_blocks", "jsonb_typeof(blocks) = 'array'");
                    table.CheckConstraint("ck_post_revisions_key_points", "jsonb_typeof(key_points) = 'array'");
                    table.CheckConstraint("ck_post_revisions_title", "char_length(title) BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "fk_post_revisions_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "content",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_post_revisions_users_saved_by",
                        column: x => x.saved_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "slug_redirects",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    post_type = table.Column<string>(type: "text", nullable: false),
                    old_slug = table.Column<string>(type: "text", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slug_redirects", x => x.id);
                    table.CheckConstraint("ck_slug_redirects_old_slug", "old_slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(old_slug) <= 150");
                    table.CheckConstraint("ck_slug_redirects_post_type", "post_type IN ('news', 'solution', 'training', 'resource', 'glossary', 'page')");
                    table.ForeignKey(
                        name: "fk_slug_redirects_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "content",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    company = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    consent_version = table.Column<string>(type: "text", nullable: false),
                    consent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "new"),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    anonymized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leads", x => x.id);
                    table.CheckConstraint("ck_leads_consent_version", "char_length(consent_version) BETWEEN 1 AND 40");
                    table.CheckConstraint("ck_leads_contact", "anonymized_at IS NOT NULL OR email IS NOT NULL OR phone IS NOT NULL");
                    table.CheckConstraint("ck_leads_course", "kind <> 'course' OR post_id IS NOT NULL");
                    table.CheckConstraint("ck_leads_email", "email IS NULL OR char_length(email) BETWEEN 3 AND 254");
                    table.CheckConstraint("ck_leads_full_name", "char_length(full_name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_leads_kind", "kind IN ('consultation', 'course')");
                    table.CheckConstraint("ck_leads_phone", "phone IS NULL OR phone ~ '^\\+?[0-9]{8,15}$'");
                    table.CheckConstraint("ck_leads_status", "status IN ('new', 'contacted', 'closed')");
                    table.CheckConstraint("ck_leads_texts", "(company IS NULL OR char_length(company) <= 200) AND (message IS NULL OR char_length(message) <= 2000) AND (user_agent IS NULL OR char_length(user_agent) <= 500) AND (note IS NULL OR char_length(note) <= 2000)");
                    table.ForeignKey(
                        name: "fk_leads_course_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "content",
                        principalTable: "course_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leads_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "content",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leads_users_handled_by",
                        column: x => x.handled_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_role",
                schema: "identity",
                table: "users",
                sql: "role IN ('customer', 'editor', 'admin')");

            migrationBuilder.CreateIndex(
                name: "ix_course_sessions_post_id",
                schema: "content",
                table: "course_sessions",
                columns: new[] { "post_id", "starts_on" });

            migrationBuilder.CreateIndex(
                name: "ix_leads_handled_by",
                schema: "content",
                table: "leads",
                column: "handled_by");

            migrationBuilder.CreateIndex(
                name: "ix_leads_post_id",
                schema: "content",
                table: "leads",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_leads_session_id",
                schema: "content",
                table: "leads",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_leads_status_created_at",
                schema: "content",
                table: "leads",
                columns: new[] { "status", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_leads_to_anonymize",
                schema: "content",
                table: "leads",
                column: "created_at",
                filter: "anonymized_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_media_file_name",
                schema: "content",
                table: "media",
                column: "file_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_sha256",
                schema: "content",
                table: "media",
                column: "sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_uploaded_by",
                schema: "content",
                table: "media",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ix_post_revisions_post_id",
                schema: "content",
                table: "post_revisions",
                columns: new[] { "post_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_post_revisions_saved_by",
                schema: "content",
                table: "post_revisions",
                column: "saved_by");

            migrationBuilder.CreateIndex(
                name: "ix_posts_admin_list",
                schema: "content",
                table: "posts",
                columns: new[] { "post_type", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_posts_author_id",
                schema: "content",
                table: "posts",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_posts_cover_media_id",
                schema: "content",
                table: "posts",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_posts_parent_id_sort_order",
                schema: "content",
                table: "posts",
                columns: new[] { "parent_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_public_list",
                schema: "content",
                table: "posts",
                columns: new[] { "post_type", "published_at" },
                descending: new[] { false, true },
                filter: "status = 'published' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_posts_search_vector",
                schema: "content",
                table: "posts",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_posts_topic_id_sort_order",
                schema: "content",
                table: "posts",
                columns: new[] { "topic_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_updated_by",
                schema: "content",
                table: "posts",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ux_posts_type_slug",
                schema: "content",
                table: "posts",
                columns: new[] { "post_type", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_slug_redirects_post_id",
                schema: "content",
                table: "slug_redirects",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ux_slug_redirects_type_old_slug",
                schema: "content",
                table: "slug_redirects",
                columns: new[] { "post_type", "old_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topics_parent_id",
                schema: "content",
                table: "topics",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ux_topics_type_parent_slug",
                schema: "content",
                table: "topics",
                columns: new[] { "post_type", "parent_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.Sql(server.Modules.Content.Persistence.ContentPrivileges.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leads",
                schema: "content");

            migrationBuilder.DropTable(
                name: "post_revisions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "slug_redirects",
                schema: "content");

            migrationBuilder.DropTable(
                name: "course_sessions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "posts",
                schema: "content");

            migrationBuilder.DropTable(
                name: "media",
                schema: "content");

            migrationBuilder.DropTable(
                name: "topics",
                schema: "content");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS content.f_blocks_text(jsonb);
                DROP FUNCTION IF EXISTS content.f_unaccent(text);
                DROP SCHEMA IF EXISTS content;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_role",
                schema: "identity",
                table: "users");

            // Quay lại bản không có editor: hạ editor về customer (xoay security stamp ⇒ token cũ mất quyền) thay vì làm hỏng CHECK.
            migrationBuilder.Sql("UPDATE identity.users SET role = 'customer', security_stamp = gen_random_uuid() WHERE role = 'editor';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_role",
                schema: "identity",
                table: "users",
                sql: "role IN ('customer', 'admin')");
        }
    }
}
