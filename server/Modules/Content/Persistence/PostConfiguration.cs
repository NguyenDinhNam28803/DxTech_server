using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Identity.Domain;

namespace server.Modules.Content.Persistence;

internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    /// <summary>Shadow property cho cột generated <c>search_vector</c>.</summary>
    public const string SearchVector = "SearchVector";

    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.ToTable("posts", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_posts_post_type", SnakeCaseEnumConverter<PostType>.CheckSql("post_type"));
            table.HasCheckConstraint("ck_posts_status", SnakeCaseEnumConverter<PostStatus>.CheckSql("status"));
            table.HasCheckConstraint("ck_posts_slug", ContentSql.SlugCheck("slug"));
            table.HasCheckConstraint("ck_posts_title", ContentSql.LengthCheck("title", Post.TitleMaxLength));
            table.HasCheckConstraint("ck_posts_excerpt", ContentSql.OptionalLength("excerpt", Post.ExcerptMaxLength));
            table.HasCheckConstraint("ck_posts_label", ContentSql.OptionalLength("label", Post.LabelMaxLength));
            table.HasCheckConstraint("ck_posts_seo", ContentSql.All(
                ContentSql.OptionalLength("seo_title", Post.SeoTitleMaxLength),
                ContentSql.OptionalLength("seo_description", Post.SeoDescriptionMaxLength)));
            table.HasCheckConstraint("ck_posts_cover_image", ContentSql.All(
                ContentSql.HttpsUrl("cover_image_url"), ContentSql.OptionalLength("cover_image_alt", 200)));
            // Kiểm chi tiết từng khối ở code (ContentBlocks); DB chốt hình dạng ngoài cùng + giới hạn số khối.
            table.HasCheckConstraint("ck_posts_blocks", $"jsonb_typeof(blocks) = 'array' AND jsonb_array_length(blocks) <= {ContentBlocks.MaxBlocks}");
            table.HasCheckConstraint("ck_posts_key_points", $"jsonb_typeof(key_points) = 'array' AND jsonb_array_length(key_points) <= {Post.MaxKeyPoints}");
            table.HasCheckConstraint("ck_posts_published_at", "status <> 'published' OR published_at IS NOT NULL");
            table.HasCheckConstraint("ck_posts_reading_minutes", "reading_minutes IS NULL OR reading_minutes BETWEEN 1 AND 600");
            table.HasCheckConstraint("ck_posts_not_own_parent", "parent_id IS DISTINCT FROM id");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.PostType).HasConversion(new SnakeCaseEnumConverter<PostType>());
        builder.Property(p => p.Status).HasConversion(new SnakeCaseEnumConverter<PostStatus>())
            .HasDefaultValue(PostStatus.Draft).HasSentinel(PostStatus.Draft);
        builder.Property(p => p.Blocks).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        builder.Property(p => p.KeyPoints)
            .HasColumnType("jsonb")
            .HasConversion(JsonListConverter.Converter, JsonListConverter.Comparer)
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(p => p.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

        builder.Property<NpgsqlTsVector>(SearchVector)
            .HasColumnName("search_vector")
            .HasComputedColumnSql(ContentSql.PostSearchVector, stored: true);

        builder.HasOne<Topic>().WithMany().HasForeignKey(p => p.TopicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Post>().WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MediaFile>().WithMany().HasForeignKey(p => p.CoverMediaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UpdatedBy).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.PostType, p.Slug })
            .IsUnique()
            .HasFilter(ContentSql.NotDeleted)
            .HasDatabaseName("ux_posts_type_slug");
        // Danh sách công khai theo loại, mới nhất trước.
        builder.HasIndex(p => new { p.PostType, p.PublishedAt })
            .IsDescending(false, true)
            .HasFilter($"status = 'published' AND {ContentSql.NotDeleted}")
            .HasDatabaseName("ix_posts_public_list");
        builder.HasIndex(p => new { p.TopicId, p.SortOrder });
        builder.HasIndex(p => new { p.ParentId, p.SortOrder });
        // Danh sách quản trị: sửa gần nhất trước.
        builder.HasIndex(p => new { p.PostType, p.UpdatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_posts_admin_list");
        builder.HasIndex(p => p.CoverMediaId);
        builder.HasIndex(p => p.AuthorId);
        builder.HasIndex(p => p.UpdatedBy);
        builder.HasIndex(SearchVector).HasMethod("gin");

        builder.HasQueryFilter(p => p.DeletedAt == null);
    }
}
