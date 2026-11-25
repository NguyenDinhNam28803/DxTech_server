using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Identity_RefreshTokenReplacedBySetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                schema: "identity",
                table: "refresh_tokens");

            migrationBuilder.AddForeignKey(
                name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "replaced_by_id",
                principalSchema: "identity",
                principalTable: "refresh_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                schema: "identity",
                table: "refresh_tokens");

            migrationBuilder.AddForeignKey(
                name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "replaced_by_id",
                principalSchema: "identity",
                principalTable: "refresh_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
