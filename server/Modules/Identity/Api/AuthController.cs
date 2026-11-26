using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Identity.Auth;
using server.Modules.Identity.Features.Auth;
using server.Shared.Results;

namespace server.Modules.Identity.Api;

/// <summary>
/// Đăng ký, đăng nhập, làm mới phiên, đăng xuất (ADR-012 §6). Access token trả trong body (client giữ trong bộ nhớ);
/// refresh token chỉ nằm trong cookie HttpOnly <c>Path=/api/auth</c>.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(RegisterHandler register, LoginHandler login, SessionHandler session) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        Result<AuthResult> result = await register.HandleAsync(request, ClientInfo.From(HttpContext), ct);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Write(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Created("/api/me", result.Value.Body);
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
        => WithCookie(await login.HandleAsync(request, ClientInfo.From(HttpContext), ct));

    /// <summary>Đổi refresh token (cookie) lấy access token mới; refresh token cũng được thay (xoay vòng).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!OriginAllowed)
        {
            return OriginRejected();
        }

        Result<AuthResult> result = await session.RefreshAsync(RefreshTokenCookie.Read(Request), ClientInfo.From(HttpContext), ct);
        if (result.IsFailure && result.Error.Type == ErrorType.Unauthorized)
        {
            RefreshTokenCookie.Delete(Response);
        }

        return WithCookie(result);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!OriginAllowed)
        {
            return OriginRejected();
        }

        await session.LogoutAsync(RefreshTokenCookie.Read(Request), ct);
        RefreshTokenCookie.Delete(Response);
        return NoContent();
    }

    private IActionResult WithCookie(Result<AuthResult> result)
    {
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Write(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(result.Value.Body);
    }

    // OriginGuard là kiểu internal của module ⇒ lấy từ DI thay vì tham số của action public.
    private bool OriginAllowed => HttpContext.RequestServices.GetRequiredService<OriginGuard>().IsAllowed(Request);

    private IActionResult OriginRejected() => this.ToProblem(
        Error.Forbidden("auth.origin_not_allowed", "Yêu cầu không đến từ trang web được phép."));
}

/// <summary>Tài khoản của người đang đăng nhập (Bearer token).</summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(AccountHandler account) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => this.ToActionResult(await account.GetAsync(UserId, ct));

    /// <summary>Đổi mật khẩu: mọi phiên khác bị thu hồi; thiết bị này nhận phiên mới (body + cookie).</summary>
    [HttpPut("password")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        Result<AuthResult> result = await account.ChangePasswordAsync(UserId, request, ClientInfo.From(HttpContext), ct);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Write(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(result.Value.Body);
    }

    /// <summary>Đăng xuất mọi thiết bị.</summary>
    [HttpPost("logout-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        Result result = await account.LogoutAllAsync(UserId, ct);
        if (result.IsSuccess)
        {
            RefreshTokenCookie.Delete(Response);
        }

        return this.ToActionResult(result);
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(AuthClaims.Subject)!);
}

/// <summary>Kiểm tra phân quyền admin (PR-8b). Trang quản trị thật ở Phase 4.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class AdminController : ControllerBase
{
    [HttpGet("ping")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public IActionResult Ping() => Ok(new { ok = true, userId = User.FindFirstValue(AuthClaims.Subject) });
}
