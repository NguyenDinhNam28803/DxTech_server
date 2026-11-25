using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Shared.Results;

namespace server.IntegrationTests.Http;

/// <summary>
/// Controller chỉ tồn tại trong test: trả mỗi loại <see cref="ErrorType"/> qua đúng đường mà
/// controller thật sẽ dùng (<see cref="ResultHttpExtensions"/>), để kiểm tra response HTTP thật.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/__test")]
public sealed class TestErrorsController : ControllerBase
{
    [HttpGet("ok")]
    public IActionResult OkResult() => this.ToActionResult(Result.Success(new { name = "Windows 11 Pro", priceVnd = 690000L }));

    [HttpPost("no-content")]
    public IActionResult NoContentResult() => this.ToActionResult(Result.Success());

    [HttpGet("errors/{type}")]
    public IActionResult Fail(ErrorType type)
    {
        Result<string> result = type == ErrorType.Validation
            ? Error.Validation(
                "test.invalid",
                "Dữ liệu không hợp lệ.",
                new Dictionary<string, string[]> { ["email"] = ["Email không hợp lệ."] })
            : new Error($"test.{type.ToString().ToLowerInvariant()}", $"Lỗi kiểm thử {type}.", type);

        return this.ToActionResult(result);
    }

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("bí mật nội bộ không được lộ ra client");
}
