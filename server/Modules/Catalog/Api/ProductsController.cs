using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Catalog.Features.Products;
using server.Shared.Paging;

namespace server.Modules.Catalog.Api;

/// <summary>Controller mỏng (ADR-003): chỉ nhận tham số, gọi handler, đổi Result sang HTTP.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/products")]
public sealed class ProductsController(ListProductsHandler list, GetProductBySlugHandler getBySlug) : ControllerBase
{
    /// <summary>Danh sách sản phẩm đang bán: lọc theo danh mục (gồm danh mục con), nền tảng, loại giấy phép, khoảng giá; phân trang.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ProductListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List([FromQuery] ListProductsQuery query, CancellationToken ct)
        => this.ToActionResult(await list.HandleAsync(query, ct));

    /// <summary>
    /// Tìm kiếm không dấu: "phan mem" khớp "Phần mềm"; gõ dở ("phan me") vẫn khớp. Mặc định xếp theo độ liên quan
    /// (tên &gt; hãng &gt; mô tả ngắn). Nhận cùng bộ lọc với danh sách; <c>q</c> bắt buộc.
    /// Lưu ý: "search" là đoạn đường dẫn cố định — không đặt slug sản phẩm là "search".
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(PagedResult<ProductListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] ListProductsQuery query, CancellationToken ct)
    {
        if (SearchText.ToTsQuery(query.Q) is null)
        {
            return this.ToProblem(server.Shared.Results.Error.Validation(
                "catalog.search_query_required",
                "Nhập từ khoá tìm kiếm (chữ hoặc số).",
                new Dictionary<string, string[]> { ["q"] = ["Từ khoá không được để trống."] }));
        }

        return this.ToActionResult(await list.HandleAsync(query, ct));
    }

    /// <summary>Chi tiết sản phẩm kèm các gói đang bán và breadcrumb danh mục. Sản phẩm ẩn/đã xoá ⇒ 404.</summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(ProductDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
        => this.ToActionResult(await getBySlug.HandleAsync(slug, ct));
}
