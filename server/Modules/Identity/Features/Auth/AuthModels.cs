using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Features.Auth;

/// <summary>Thông tin tài khoản trả cho client — không có hash, security stamp, bộ đếm đăng nhập sai.</summary>
public sealed record UserProfile(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    string? Phone,
    string? CompanyName,
    string? TaxCode,
    bool EmailConfirmed,
    DateTimeOffset CreatedAt)
{
    internal static UserProfile From(User u) => new(
        u.Id, u.Email, u.FullName, UserRoles.Name(u.Role),
        u.Phone, u.CompanyName, u.TaxCode, u.EmailConfirmedAt is not null, u.CreatedAt);
}

/// <summary>Body JSON sau đăng ký/đăng nhập/refresh. Refresh token KHÔNG nằm ở đây — chỉ trong cookie HttpOnly.</summary>
public sealed record AuthResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, UserProfile User);

/// <summary>Kết quả nội bộ của handler: body + refresh token để controller ghi cookie.</summary>
public sealed record AuthResult(AuthResponse Body, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt)
{
    internal static AuthResult From(server.Modules.Identity.Auth.IssuedSession s) => new(
        new AuthResponse(s.AccessToken, "Bearer", s.AccessTokenExpiresAt, UserProfile.From(s.User)),
        s.RefreshToken,
        s.RefreshTokenExpiresAt);
}

public sealed record RegisterRequest(string? Email, string? Password, string? FullName);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
