using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Identity.Contracts;
using server.Modules.Sales.Features.Orders;
using server.Shared.Results;

namespace server.Modules.Sales.Api;

/// <summary>Đơn hàng (PR-11). Bắt buộc đăng nhập (phase3-schema.md mục 0, quyết định 2).</summary>
[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrdersController(CreateOrderHandler create, GetOrderHandler get, CancelOrderHandler cancel) : ControllerBase
{
    /// <summary>
    /// Đặt hàng. Header <c>Idempotency-Key</c> (UUID, web sinh một lần mỗi lần mở trang thanh toán) bắt buộc:
    /// gửi lại cùng khoá ⇒ trả lại đơn cũ (200) thay vì tạo đơn mới. Giá, tồn kho tính lại ở server; key được giữ ngay.
    /// 201: đơn mới kèm hướng dẫn chuyển khoản VietQR, hạn 30 phút.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, [FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(idempotencyKey, out Guid key) || key == Guid.Empty)
        {
            return this.ToProblem(Error.Validation("sales.idempotency_key_required", "Thiếu header Idempotency-Key (UUID).",
                new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Bắt buộc, dạng UUID."] }));
        }

        Result<CreateOrderResult> result = await create.HandleAsync(UserId, key, request, ct);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return result.Value.Created
            ? CreatedAtAction(nameof(Get), new { code = result.Value.Order.Code }, result.Value.Order)
            : Ok(result.Value.Order);
    }

    /// <summary>Chi tiết đơn của chính mình (web hỏi mỗi 3 giây khi chờ thanh toán). Key hiện khi đơn đã giao.</summary>
    [HttpGet("{code}")]
    [ProducesResponseType(typeof(OrderView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string code, CancellationToken ct)
        => this.ToActionResult(await get.HandleAsync(UserId, code, ct));

    /// <summary>Huỷ đơn đang chờ thanh toán; key về kho ngay.</summary>
    [HttpPost("{code}/cancel")]
    [ProducesResponseType(typeof(OrderView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(string code, CancellationToken ct)
        => this.ToActionResult(await cancel.HandleAsync(UserId, code, ct));

    private Guid UserId => CurrentUser.IdOf(User) ?? throw new InvalidOperationException("Thiếu claim sub sau [Authorize].");
}

/// <summary>"Đơn của tôi".</summary>
[ApiController]
[Authorize]
[Route("api/me/orders")]
public sealed class MyOrdersController(ListMyOrdersHandler list) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(OrderPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? cursor, CancellationToken ct)
        => this.ToActionResult(await list.HandleAsync(CurrentUser.IdOf(User)!.Value, cursor, ct));
}
