using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Sales.Features.Cart;

namespace server.Modules.Sales.Api;

/// <summary>Giỏ hàng (PR-10). Không cần đăng nhập: khách xem giá trước khi đăng nhập để đặt hàng.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/cart")]
public sealed class CartController(QuoteCartHandler quote) : ControllerBase
{
    /// <summary>
    /// Báo giá giỏ hàng: giá, tồn kho, tổng tiền đều tính ở server. Dòng không bán được / thiếu hàng có trạng thái riêng,
    /// không làm cả request lỗi. Giới hạn 60 lần/phút/IP.
    /// </summary>
    [HttpPost("quote")]
    [ProducesResponseType(typeof(CartQuote), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Quote([FromBody] CartQuoteRequest request, CancellationToken ct)
        => this.ToActionResult(await quote.HandleAsync(request, ct));
}
