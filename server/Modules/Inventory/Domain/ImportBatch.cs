using server.Shared.Ids;

namespace server.Modules.Inventory.Domain;

/// <summary>
/// Một lần nhập key (bảng <c>inventory.import_batches</c>): ai nhập, nguồn nào, bao nhiêu dòng hợp lệ/trùng/lỗi.
/// Không lưu file gốc (chứa key rõ).
/// </summary>
internal sealed class ImportBatch
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private ImportBatch()
    {
        FileName = null!;
    }

    public ImportBatch(Guid variantId, ImportSource source, string fileName, DateTimeOffset createdAt, Guid? importedBy = null, string? note = null)
    {
        if (source == ImportSource.Admin && importedBy is null)
        {
            throw new ArgumentException("Nhập từ admin phải có người nhập.", nameof(importedBy));
        }

        Id = UuidV7.NewGuid(createdAt);
        VariantId = variantId;
        Source = source;
        FileName = fileName;
        CreatedAt = createdAt;
        ImportedBy = importedBy;
        Note = note;
    }

    public Guid Id { get; private set; }

    public Guid VariantId { get; private set; }

    public ImportSource Source { get; private set; }

    public string FileName { get; private set; }

    public int TotalRows { get; private set; }

    public int InsertedRows { get; private set; }

    public int DuplicateRows { get; private set; }

    public int InvalidRows { get; private set; }

    public Guid? ImportedBy { get; private set; }

    /// <summary>Ví dụ số hoá đơn mua từ nhà phân phối.</summary>
    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Ghi kết quả sau khi xử lý file. Tổng phải bằng tổng ba loại (CHECK trong DB).</summary>
    public void Record(int inserted, int duplicates, int invalid)
    {
        InsertedRows = inserted;
        DuplicateRows = duplicates;
        InvalidRows = invalid;
        TotalRows = inserted + duplicates + invalid;
    }
}
