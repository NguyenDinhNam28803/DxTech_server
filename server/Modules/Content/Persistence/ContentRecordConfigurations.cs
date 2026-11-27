using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Identity.Domain;

namespace server.Modules.Content.Persistence;

internal sealed class PostRevisionConfiguration : IEntityTypeConfiguration<PostRevision>
{
    public void Configure(EntityTypeBuilder<PostRevision> builder)
    {
        builder.ToTable("post_revisions", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_post_revisions_title", ContentSql.LengthCheck("title", Post.TitleMaxLength));
            table.HasCheckConstraint("ck_post_revisions_blocks", "jsonb_typeof(blocks) = 'array'");
            table.HasCheckConstraint("ck_post_revisions_key_points", "jsonb_typeof(key_points) = 'array'");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityAlwaysColumn();
        builder.Property(r => r.Blocks).HasColumnType("jsonb");
        builder.Property(r => r.KeyPoints)
            .HasColumnType("jsonb")
            .HasConversion(JsonListConverter.Converter, JsonListConverter.Comparer);
        builder.Property(r => r.SavedAt).HasDefaultValueSql("now()");

        // Xoá bài là xoá mềm ⇒ revision không bao giờ mồ côi; Restrict để không ai xoá cứng bài còn lịch sử.
        builder.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.SavedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.PostId, r.Id }).IsDescending(false, true).HasDatabaseName("ix_post_revisions_post_id");
        builder.HasIndex(r => r.SavedBy);
    }
}

internal sealed class SlugRedirectConfiguration : IEntityTypeConfiguration<SlugRedirect>
{
    public void Configure(EntityTypeBuilder<SlugRedirect> builder)
    {
        builder.ToTable("slug_redirects", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_slug_redirects_post_type", SnakeCaseEnumConverter<PostType>.CheckSql("post_type"));
            table.HasCheckConstraint("ck_slug_redirects_old_slug", ContentSql.SlugCheck("old_slug"));
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityAlwaysColumn();
        builder.Property(r => r.PostType).HasConversion(new SnakeCaseEnumConverter<PostType>());
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.PostType, r.OldSlug }).IsUnique().HasDatabaseName("ux_slug_redirects_type_old_slug");
        builder.HasIndex(r => r.PostId);
    }
}

internal sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
{
    public void Configure(EntityTypeBuilder<MediaFile> builder)
    {
        string types = string.Join(", ", MediaFile.ContentTypes.Select(t => $"'{t}'"));
        builder.ToTable("media", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_media_content_type", $"content_type IN ({types})");
            table.HasCheckConstraint("ck_media_file_name", "file_name ~ '^[0-9a-f]{32}\\.(jpg|png|webp)$'");
            table.HasCheckConstraint("ck_media_bytes", $"bytes BETWEEN 1 AND {MediaFile.MaxBytes}");
            table.HasCheckConstraint("ck_media_dimensions", $"width BETWEEN 1 AND {MediaFile.MaxDimension} AND height BETWEEN 1 AND {MediaFile.MaxDimension}");
            table.HasCheckConstraint("ck_media_sha256", "octet_length(sha256) = 32");
            table.HasCheckConstraint("ck_media_alt_default", ContentSql.OptionalLength("alt_default", 200));
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => m.FileName).IsUnique();
        // Chống tải trùng một ảnh (kể cả ảnh đã xoá mềm: khôi phục thay vì tải lại).
        builder.HasIndex(m => m.Sha256).IsUnique();
        builder.HasIndex(m => m.UploadedBy);

        builder.HasQueryFilter(m => m.DeletedAt == null);
    }
}

internal sealed class CourseSessionConfiguration : IEntityTypeConfiguration<CourseSession>
{
    public void Configure(EntityTypeBuilder<CourseSession> builder)
    {
        builder.ToTable("course_sessions", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_course_sessions_format", SnakeCaseEnumConverter<CourseFormat>.CheckSql("format"));
            table.HasCheckConstraint("ck_course_sessions_status", SnakeCaseEnumConverter<CourseSessionStatus>.CheckSql("status"));
            table.HasCheckConstraint("ck_course_sessions_dates", "ends_on IS NULL OR ends_on >= starts_on");
            table.HasCheckConstraint("ck_course_sessions_seats", "seats IS NULL OR seats > 0");
            table.HasCheckConstraint("ck_course_sessions_price", "price_vnd IS NULL OR price_vnd >= 0");
            table.HasCheckConstraint("ck_course_sessions_location", ContentSql.OptionalLength("location", 200));
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Format).HasConversion(new SnakeCaseEnumConverter<CourseFormat>());
        builder.Property(s => s.Status).HasConversion(new SnakeCaseEnumConverter<CourseSessionStatus>())
            .HasDefaultValue(CourseSessionStatus.Open).HasSentinel(CourseSessionStatus.Open);
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne<Post>().WithMany().HasForeignKey(s => s.PostId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.PostId, s.StartsOn }).HasDatabaseName("ix_course_sessions_post_id");
    }
}

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.ToTable("leads", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_leads_kind", SnakeCaseEnumConverter<LeadKind>.CheckSql("kind"));
            table.HasCheckConstraint("ck_leads_status", SnakeCaseEnumConverter<LeadStatus>.CheckSql("status"));
            table.HasCheckConstraint("ck_leads_full_name", ContentSql.LengthCheck("full_name", 100));
            // Ẩn danh hoá xoá cả email lẫn điện thoại ⇒ "ít nhất một liên hệ" chỉ áp dụng khi chưa ẩn danh.
            table.HasCheckConstraint("ck_leads_contact", "anonymized_at IS NOT NULL OR email IS NOT NULL OR phone IS NOT NULL");
            table.HasCheckConstraint("ck_leads_email", "email IS NULL OR char_length(email) BETWEEN 3 AND 254");
            table.HasCheckConstraint("ck_leads_phone", "phone IS NULL OR phone ~ '^\\+?[0-9]{8,15}$'");
            table.HasCheckConstraint("ck_leads_texts",
                $"(company IS NULL OR char_length(company) <= 200) AND (message IS NULL OR char_length(message) <= {Lead.MessageMaxLength}) " +
                "AND (user_agent IS NULL OR char_length(user_agent) <= 500) AND (note IS NULL OR char_length(note) <= 2000)");
            table.HasCheckConstraint("ck_leads_consent_version", ContentSql.LengthCheck("consent_version", 40));
            table.HasCheckConstraint("ck_leads_course", "kind <> 'course' OR post_id IS NOT NULL");
        });

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Kind).HasConversion(new SnakeCaseEnumConverter<LeadKind>());
        builder.Property(l => l.Status).HasConversion(new SnakeCaseEnumConverter<LeadStatus>())
            .HasDefaultValue(LeadStatus.New).HasSentinel(LeadStatus.New);
        builder.Property(l => l.Email).HasColumnType("citext");
        builder.Property(l => l.SourceIp).HasColumnType("inet");
        builder.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne<Post>().WithMany().HasForeignKey(l => l.PostId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CourseSession>().WithMany().HasForeignKey(l => l.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.HandledBy).OnDelete(DeleteBehavior.Restrict);

        // Hàng chờ xử lý trong admin; job ẩn danh hoá quét theo created_at.
        builder.HasIndex(l => new { l.Status, l.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_leads_status_created_at");
        builder.HasIndex(l => l.CreatedAt).HasFilter("anonymized_at IS NULL").HasDatabaseName("ix_leads_to_anonymize");
        builder.HasIndex(l => l.PostId);
        builder.HasIndex(l => l.SessionId);
        builder.HasIndex(l => l.HandledBy);
    }
}
