using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace server.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR-3 (ADR-002, 006, 007, 008): schema <c>catalog</c> với 3 bảng categories, products, product_variants;
    /// hàm <c>catalog.f_unaccent</c> + cột generated <c>search_vector</c> + index GIN cho tìm kiếm không dấu;
    /// 6 danh mục gốc (D16: dữ liệu cấu hình nằm trong migration, sản phẩm demo đi qua seeder PR-5).
    /// Chạy bằng role <c>dxtechs_owner</c> (ADR-009): extension <c>unaccent</c> đã được roles.sql tạo sẵn.
    /// </summary>
    public partial class Catalog_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            // Phải có trước bảng products: cột generated search_vector gọi hàm này.
            // unaccent() chỉ là STABLE nên không dùng được trong generated column/index ⇒ bọc thành IMMUTABLE,
            // chỉ định rõ từ điển public.unaccent. Thân hàm kiểu chuẩn SQL "RETURN <biểu thức>" (PG ≥ 14): được phân
            // tích ngay lúc tạo, không phụ thuộc search_path của phiên. Không dùng BEGIN ATOMIC … ; END vì Npgsql
            // tách lệnh theo dấu ";" và làm vỡ thân hàm.
            // SQL chép nguyên văn (không tham chiếu hằng số trong code) để migration không đổi theo code sau này.
            migrationBuilder.Sql("""
                CREATE FUNCTION catalog.f_unaccent(input text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                    RETURN public.unaccent('public.unaccent'::regdictionary, input);
                """);

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_name", "char_length(name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_categories_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_categories_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "fk_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    short_description = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    brand = table.Column<string>(type: "text", nullable: true),
                    license_type = table.Column<string>(type: "text", nullable: false),
                    platforms = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    features = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    badge = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(name, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(brand, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(short_description, ''))), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_features_array", "jsonb_typeof(features) = 'array'");
                    table.CheckConstraint("ck_products_license_type", "license_type IN ('perpetual', 'subscription', 'service')");
                    table.CheckConstraint("ck_products_name", "char_length(name) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_products_platforms", "platforms <@ ARRAY['windows', 'macos', 'linux', 'ios', 'android', 'web']::text[]");
                    table.CheckConstraint("ck_products_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "fk_products_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    term_months = table.Column<int>(type: "integer", nullable: true),
                    seats = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    compare_at_price_vnd = table.Column<long>(type: "bigint", nullable: true),
                    delivery_type = table.Column<string>(type: "text", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.CheckConstraint("ck_product_variants_compare_at_price", "compare_at_price_vnd IS NULL OR compare_at_price_vnd > price_vnd");
                    table.CheckConstraint("ck_product_variants_delivery_type", "delivery_type IN ('license_key', 'account', 'manual')");
                    table.CheckConstraint("ck_product_variants_name", "char_length(name) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_product_variants_price", "price_vnd >= 0");
                    table.CheckConstraint("ck_product_variants_seats", "seats > 0");
                    table.CheckConstraint("ck_product_variants_sku", "sku ~ '^[A-Za-z0-9][A-Za-z0-9._-]*$'");
                    table.CheckConstraint("ck_product_variants_term_months", "term_months IS NULL OR term_months > 0");
                    table.ForeignKey(
                        name: "fk_product_variants_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id",
                schema: "catalog",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_slug",
                schema: "catalog",
                table: "categories",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id_price_vnd",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "product_id", "price_vnd" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_sku",
                schema: "catalog",
                table: "product_variants",
                column: "sku",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_one_default",
                schema: "catalog",
                table: "product_variants",
                column: "product_id",
                unique: true,
                filter: "is_default AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id_listed",
                schema: "catalog",
                table: "products",
                column: "category_id",
                filter: "deleted_at IS NULL AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_products_platforms",
                schema: "catalog",
                table: "products",
                column: "platforms")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_products_search_vector",
                schema: "catalog",
                table: "products",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_products_slug",
                schema: "catalog",
                table: "products",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            // Danh mục gốc — khớp id danh mục mà client đang dùng (src/data/products.ts). Id cố định để
            // seeder (PR-5) và dữ liệu thật tham chiếu ổn định giữa các môi trường.
            migrationBuilder.Sql("""
                INSERT INTO catalog.categories (id, slug, name, description, sort_order) VALUES
                    ('01a1fbe1-6754-7541-941d-ea90e3341d07', 'software', 'Phần mềm bản quyền', 'Hệ điều hành, ứng dụng văn phòng, thiết kế và bảo mật', 10),
                    ('01a1fbe1-6755-7910-ad9b-b6a54662e9e7', 'cloud', 'Cloud & Hosting', 'Máy chủ đám mây NVMe và hosting tốc độ cao LiteSpeed', 20),
                    ('01a1fbe1-6756-7684-9793-dae8d847ae8a', 'domain', 'Tên miền', 'Đăng ký và bảo vệ thương hiệu trực tuyến toàn cầu', 30),
                    ('01a1fbe1-6757-7db9-bfca-591a2b816890', 'ssl', 'SSL & Bảo mật', 'Chứng chỉ mã hóa bảo mật website và xác thực doanh nghiệp', 40),
                    ('01a1fbe1-6758-778f-b61e-991c16fdca2c', 'storage', 'Lưu trữ đám mây', 'Bộ nhớ lưu trữ doanh nghiệp, đồng bộ tệp an toàn', 50),
                    ('01a1fbe1-6759-7d85-b200-c5eebae07ed6', 'backup', 'Sao lưu & Phục hồi', 'Giải pháp sao lưu tự động và khôi phục dữ liệu thảm họa', 60);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS catalog.f_unaccent(text);");

            // Không DROP EXTENSION unaccent: extension do roles.sql (superuser) quản lý.
        }
    }
}
