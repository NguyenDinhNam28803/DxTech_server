using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Persistence;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", IdentityModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_refresh_tokens_hash_len", $"octet_length(token_hash) = {RefreshToken.HashLength}");
            table.HasCheckConstraint("ck_refresh_tokens_expiry", "expires_at > created_at AND expires_at <= session_expires_at");
            table.HasCheckConstraint("ck_refresh_tokens_revoked_reason",
                "revoked_reason IS NULL OR " + SnakeCaseEnumConverter<RefreshTokenRevokedReason>.CheckSql("revoked_reason"));
            // Thu hồi luôn có lý do và ngược lại.
            table.HasCheckConstraint("ck_refresh_tokens_revoked", "(revoked_at IS NULL) = (revoked_reason IS NULL)");
            table.HasCheckConstraint("ck_refresh_tokens_user_agent", "user_agent IS NULL OR char_length(user_agent) <= 512");
            table.HasCheckConstraint("ck_refresh_tokens_not_own_replacement", "replaced_by_id IS NULL OR replaced_by_id <> id");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).IsRequired();
        builder.Property(t => t.RevokedReason).HasConversion(new SnakeCaseEnumConverter<RefreshTokenRevokedReason>());
        builder.Property(t => t.CreatedIp).HasColumnType("inet");

        // Xoá tài khoản ⇒ phiên đăng nhập không còn ý nghĩa: CASCADE là chủ đích (dữ liệu kỹ thuật, không phải chứng từ).
        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // SET NULL (PR-8): token trong một họ trỏ vào nhau; RESTRICT làm hỏng việc xoá tài khoản (CASCADE nhiều dòng)
        // và job dọn token theo lô.
        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(t => t.ReplacedById)
            .OnDelete(DeleteBehavior.SetNull);

        // POST /api/auth/refresh tra theo hash.
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");

        // Đăng xuất mọi thiết bị / danh sách phiên đang mở. Thay index FK mặc định trên user_id.
        builder.HasIndex(t => t.UserId)
            .HasDatabaseName("ix_refresh_tokens_user_active")
            .HasFilter("revoked_at IS NULL");

        // Thu hồi cả họ khi phát hiện dùng lại token.
        builder.HasIndex(t => t.FamilyId);

        // Job dọn token hết hạn hằng ngày.
        builder.HasIndex(t => t.ExpiresAt);
    }
}
