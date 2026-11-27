using Microsoft.EntityFrameworkCore;

namespace server.Modules.Content.Persistence;

/// <summary>Hàm SQL của schema <c>content</c> dùng được trong LINQ (tạo ở migration Content_Initial).</summary>
internal static class ContentDbFunctions
{
    /// <summary><c>content.f_unaccent(text)</c> — chỉ dùng trong truy vấn EF.</summary>
    public static string Unaccent(string input)
        => throw new InvalidOperationException("content.f_unaccent chỉ dùng trong truy vấn EF.");

    public static void Register(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(ContentDbFunctions).GetMethod(nameof(Unaccent))!)
            .HasName("f_unaccent")
            .HasSchema(ContentModule.Schema);
    }
}
