using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Catalog.Features.Categories;

namespace server.Modules.Catalog.Api;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController(GetCategoryTreeHandler getTree) : ControllerBase
{
    /// <summary>Cây danh mục đang hiển thị (gốc → con), sắp theo thứ tự hiển thị.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryNode>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTree(CancellationToken ct)
        => this.ToActionResult(await getTree.HandleAsync(ct));
}
