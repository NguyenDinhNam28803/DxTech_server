using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using server.Infrastructure.Options;
using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Auth;

/// <summary>Tên claim trong access token (ADR-012 §1). Giữ ngắn vì token đi kèm mọi request.</summary>
internal static class AuthClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Role = "role";
    /// <summary>8 ký tự đầu của security stamp — policy Admin so với DB để thu hồi quyền gần như ngay.</summary>
    public const string StampPrefix = "sst";

    public static string StampOf(User user) => user.SecurityStamp.ToString("N")[..8];
}

/// <summary>
/// Ký access token HS256 bằng khoá 0; khoá ở mọi vị trí đều được chấp nhận khi kiểm tra (xoay khoá không đăng xuất ai).
/// <c>kid</c> = 8 ký tự hex đầu của SHA-256(khoá) — không lộ khoá, đủ để chọn đúng khoá khi kiểm tra.
/// </summary>
internal sealed class AccessTokenService
{
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly SigningCredentials _signing;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessTokenService(IOptions<AuthOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _signing = new SigningCredentials(SigningKeys(_options)[0], SecurityAlgorithms.HmacSha256);
    }

    public static IReadOnlyList<SymmetricSecurityKey> SigningKeys(AuthOptions options)
        => options.ActiveSigningKeys
            .Select(k =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(k);
                return new SymmetricSecurityKey(bytes) { KeyId = Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant() };
            })
            .ToList();

    public static TokenValidationParameters ValidationParameters(AuthOptions options) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKeys = SigningKeys(options),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuerSigningKey = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = AuthClaims.Subject,
        RoleClaimType = AuthClaims.Role,
    };

    public (string Token, DateTimeOffset ExpiresAt) Create(User user)
    {
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset expires = now.AddMinutes(_options.AccessTokenMinutes);
        string token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _signing,
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaims.Subject, user.Id.ToString()),
                new Claim(AuthClaims.Role, user.Role == UserRole.Admin ? "admin" : "customer"),
                new Claim(AuthClaims.StampPrefix, AuthClaims.StampOf(user)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        });
        return (token, expires);
    }
}
