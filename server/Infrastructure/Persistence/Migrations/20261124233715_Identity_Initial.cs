using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Identity_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    email_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: true),
                    company_name = table.Column<string>(type: "text", nullable: true),
                    tax_code = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<Guid>(type: "uuid", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_company_name", "company_name IS NULL OR char_length(company_name) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_users_email", "char_length(email) BETWEEN 3 AND 254 AND position('@' in email) > 1 AND email !~ '\\s'");
                    table.CheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0");
                    table.CheckConstraint("ck_users_full_name", "char_length(full_name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_users_password_hash", "char_length(password_hash) BETWEEN 20 AND 500");
                    table.CheckConstraint("ck_users_phone", "phone IS NULL OR phone ~ '^\\+?[0-9]{8,15}$'");
                    table.CheckConstraint("ck_users_role", "role IN ('customer', 'admin')");
                    table.CheckConstraint("ck_users_status", "status IN ('active', 'locked', 'disabled')");
                    table.CheckConstraint("ck_users_tax_code", "tax_code IS NULL OR tax_code ~ '^[0-9]{10}(-?[0-9]{3})?$'");
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "text", nullable: true),
                    created_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.CheckConstraint("ck_refresh_tokens_expiry", "expires_at > created_at AND expires_at <= session_expires_at");
                    table.CheckConstraint("ck_refresh_tokens_hash_len", "octet_length(token_hash) = 32");
                    table.CheckConstraint("ck_refresh_tokens_not_own_replacement", "replaced_by_id IS NULL OR replaced_by_id <> id");
                    table.CheckConstraint("ck_refresh_tokens_revoked", "(revoked_at IS NULL) = (revoked_reason IS NULL)");
                    table.CheckConstraint("ck_refresh_tokens_revoked_reason", "revoked_reason IS NULL OR revoked_reason IN ('rotated', 'logout', 'reuse_detected', 'password_changed', 'admin')");
                    table.CheckConstraint("ck_refresh_tokens_user_agent", "user_agent IS NULL OR char_length(user_agent) <= 512");
                    table.ForeignKey(
                        name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalSchema: "identity",
                        principalTable: "refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                schema: "identity",
                table: "refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_replaced_by_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_active",
                schema: "identity",
                table: "refresh_tokens",
                column: "user_id",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_created_at",
                schema: "identity",
                table: "users",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_users_role_admin",
                schema: "identity",
                table: "users",
                column: "role",
                filter: "role = 'admin'");

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                schema: "identity",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
