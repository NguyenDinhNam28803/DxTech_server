using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;

namespace server.Modules.Content.Persistence;

internal sealed class TopicConfiguration : IEntityTypeConfiguration<Topic>
{
    public void Configure(EntityTypeBuilder<Topic> builder)
    {
        builder.ToTable("topics", ContentModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_topics_post_type", SnakeCaseEnumConverter<PostType>.CheckSql("post_type"));
            table.HasCheckConstraint("ck_topics_slug", ContentSql.SlugCheck("slug"));
            table.HasCheckConstraint("ck_topics_name", ContentSql.LengthCheck("name", Topic.NameMaxLength));
            table.HasCheckConstraint("ck_topics_description", ContentSql.OptionalLength("description", Topic.DescriptionMaxLength));
            table.HasCheckConstraint("ck_topics_image_url", ContentSql.HttpsUrl("image_url"));
            table.HasCheckConstraint("ck_topics_not_own_parent", "parent_id IS DISTINCT FROM id");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.PostType).HasConversion(new SnakeCaseEnumConverter<PostType>());
        builder.Property(t => t.IsActive).HasDefaultValue(true).HasSentinel(true);
        builder.Property(t => t.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne<Topic>().WithMany().HasForeignKey(t => t.ParentId).OnDelete(DeleteBehavior.Restrict);

        // Slug duy nhất trong cùng loại + cùng cha (chủ đề gốc: parent_id NULL ⇒ cần NULLS NOT DISTINCT, PG 15+).
        builder.HasIndex(t => new { t.PostType, t.ParentId, t.Slug })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter(ContentSql.NotDeleted)
            .HasDatabaseName("ux_topics_type_parent_slug");
        builder.HasIndex(t => t.ParentId);

        builder.HasQueryFilter(t => t.DeletedAt == null);
    }
}
