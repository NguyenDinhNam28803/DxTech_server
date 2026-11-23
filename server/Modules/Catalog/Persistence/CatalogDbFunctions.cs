using Microsoft.EntityFrameworkCore;

namespace server.Modules.Catalog.Persistence;

/// <summary>
/// Hàm SQL của schema <c>catalog</c> dùng được trong LINQ. Chỉ để EF dịch sang SQL — gọi trực tiếp trong C# sẽ ném lỗi.
/// </summary>
internal static class CatalogDbFunctions
{
    /// <summary><c>catalog.f_unaccent(text)</c>: "Phần mềm ĐỒ HỌA" → "Phan mem DO HOA" (tạo ở migration Catalog_Initial).</summary>
    public static string Unaccent(string input)
        => throw new InvalidOperationException("catalog.f_unaccent chỉ dùng trong truy vấn EF.");

    public static void Register(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(CatalogDbFunctions).GetMethod(nameof(Unaccent))!)
            .HasName("f_unaccent")
            .HasSchema(CatalogModule.Schema);
    }
}
