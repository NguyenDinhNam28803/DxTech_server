using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", IdentityModule.Schema, table =>
        {
            // Lớp phòng thủ cuối: code đã chuẩn hoá email, nhưng mọi đường ghi (SQL tay, module sau) đều qua đây.
            table.HasCheckConstraint("ck_users_email",
                "char_length(email) BETWEEN 3 AND 254 AND position('@' in email) > 1 AND email !~ '\\s'");
            table.HasCheckConstraint("ck_users_full_name", "char_length(full_name) BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_users_password_hash", "char_length(password_hash) BETWEEN 20 AND 500");
            table.HasCheckConstraint("ck_users_phone", "phone IS NULL OR phone ~ '^\\+?[0-9]{8,15}$'");
            table.HasCheckConstraint("ck_users_company_name", "company_name IS NULL OR char_length(company_name) BETWEEN 1 AND 200");
            table.HasCheckConstraint("ck_users_tax_code", "tax_code IS NULL OR tax_code ~ '^[0-9]{10}(-?[0-9]{3})?$'");
            table.HasCheckConstraint("ck_users_role", SnakeCaseEnumConverter<UserRole>.CheckSql("role"));
            table.HasCheckConstraint("ck_users_status", SnakeCaseEnumConverter<UserStatus>.CheckSql("status"));
            table.HasCheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0");
        });

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Email).IsRequired().HasColumnType("citext");
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.FullName).IsRequired();
        builder.Property(u => u.Role).HasConversion(new SnakeCaseEnumConverter<UserRole>());
        builder.Property(u => u.Status).HasConversion(new SnakeCaseEnumConverter<UserStatus>());
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");

        builder.Property(u => u.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        // Đăng nhập, chống đăng ký trùng. citext ⇒ "A@X.com" và "a@x.com" là một.
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ux_users_email");

        // Danh sách khách trong admin, phân trang keyset (mới nhất trước).
        builder.HasIndex(u => new { u.CreatedAt, u.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_users_created_at");

        // Liệt kê admin — index rất nhỏ vì chỉ chứa vài dòng.
        builder.HasIndex(u => u.Role)
            .HasDatabaseName("ix_users_role_admin")
            .HasFilter("role = 'admin'");
    }
}
