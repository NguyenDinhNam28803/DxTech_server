using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace server.Infrastructure.Persistence.Conventions;

/// <summary>
/// Đặt tên bảng, cột, khoá, khoá ngoại và index theo snake_case (ADR-006, quyết định D3),
/// để SQL viết tay không phải đặt tên trong nháy kép ("ProductVariants").
/// Tự viết thay vì dùng package EFCore.NamingConventions (bên thứ ba).
/// Tên được cấu hình tường minh (ToTable/HasColumnName/HasDatabaseName...) được giữ nguyên.
/// </summary>
public sealed class SnakeCaseNamingConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            // Entity owned lưu chung bảng với chủ, hoặc entity không map ra bảng: không đổi tên bảng.
            if (entityType.GetTableName() is { } tableName
                && entityType.GetTableNameConfigurationSource() != ConfigurationSource.Explicit
                && !entityType.IsOwned())
            {
                entityType.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.GetColumnNameConfigurationSource() != ConfigurationSource.Explicit
                    && property.GetColumnName() is { } columnName)
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            foreach (var key in entityType.GetDeclaredKeys())
            {
                if (key.GetNameConfigurationSource() != ConfigurationSource.Explicit && key.GetName() is { } name)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entityType.GetDeclaredForeignKeys())
            {
                if (foreignKey.GetConstraintNameConfigurationSource() != ConfigurationSource.Explicit
                    && foreignKey.GetConstraintName() is { } name)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entityType.GetDeclaredIndexes())
            {
                if (index.GetDatabaseNameConfigurationSource() != ConfigurationSource.Explicit
                    && index.GetDatabaseName() is { } name)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// "ProductVariants" → "product_variants", "PK_Products" → "pk_products",
    /// "IX_Products_CategoryId" → "ix_products_category_id", "HTMLParser" → "html_parser",
    /// "Sha256Hash" → "sha256_hash".
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];
            if (char.IsUpper(current))
            {
                bool hasPrevious = i > 0 && name[i - 1] != '_';
                bool previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool previousIsUpper = i > 0 && char.IsUpper(name[i - 1]);
                bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                // Ranh giới từ: "aB" hoặc cuối một cụm viết hoa "HTMLParser" (L|P).
                if (hasPrevious && (previousIsLowerOrDigit || (previousIsUpper && nextIsLower)))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
