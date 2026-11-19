namespace server.Shared.Paging;

/// <summary>
/// Tham số phân trang kiểu offset — dùng cho danh mục sản phẩm (cần số trang, dữ liệu nhỏ).
/// Giá trị sai (trang &lt; 1, kích thước ngoài 1..MaxPageSize) được kẹp về giới hạn thay vì báo lỗi,
/// để link cũ hoặc tham số do người dùng sửa tay vẫn trả được trang hợp lệ.
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        Page = Math.Max(1, page);
        PageSize = Math.Clamp(pageSize, 1, MaxPageSize);
    }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>Số dòng bỏ qua (OFFSET).</summary>
    public int Skip => (Page - 1) * PageSize;
}

/// <summary>Một trang kết quả: định dạng response chung <c>{ items, page, pageSize, total, totalPages }</c>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => Total == 0 ? 0 : (int)((Total + PageSize - 1) / PageSize);

    public static PagedResult<T> Empty(PageRequest request) => new([], request.Page, request.PageSize, 0);
}
