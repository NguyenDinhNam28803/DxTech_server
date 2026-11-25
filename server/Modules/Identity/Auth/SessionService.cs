using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Infrastructure.Options;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Domain;
using server.Shared.Results;

namespace server.Modules.Identity.Auth;

/// <summary>Thông tin thiết bị gửi request — hiển thị “thiết bị đang đăng nhập”, không dùng để xác thực.</summary>
internal sealed record ClientInfo(IPAddress? Ip, string? UserAgent)
{
    public static ClientInfo From(HttpContext http)
    {
        string? ua = http.Request.Headers.UserAgent;
        return new ClientInfo(http.Connection.RemoteIpAddress, ua is { Length: > 512 } ? ua[..512] : ua);
    }
}

/// <summary>Phiên vừa cấp: access token cho body JSON, refresh token cho cookie.</summary>
internal sealed record IssuedSession(
    User User,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Cấp, xoay vòng và thu hồi refresh token (ADR-012 §1). Token gốc = 32 byte ngẫu nhiên (base64url) chỉ trả về cho
/// client; DB lưu SHA-256. Xoay vòng chạy trong transaction có khoá dòng (<c>FOR UPDATE</c>) nên hai request dùng
/// cùng token không thể cùng được cấp token mới.
/// </summary>
internal sealed class SessionService(
    AppDbContext db,
    AccessTokenService accessTokens,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<SessionService> logger)
{
    private readonly AuthOptions _options = options.Value;

    public static byte[] Hash(string rawToken) => SHA256.HashData(Encoding.ASCII.GetBytes(rawToken));

    /// <summary>Đăng nhập/đăng ký: mở họ token mới, hạn tuyệt đối = bây giờ + SessionDays.</summary>
    public async Task<IssuedSession> StartAsync(User user, ClientInfo client, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        (RefreshToken token, string raw) = NewToken(user.Id, Guid.NewGuid(), now, now.AddDays(_options.SessionDays), client);
        db.Add(token);
        await db.SaveChangesAsync(ct);
        return Issue(user, token, raw);
    }

    public async Task<Result<IssuedSession>> RotateAsync(string rawToken, ClientInfo client, CancellationToken ct)
    {
        byte[] hash = Hash(rawToken);
        DateTimeOffset now = time.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        RefreshToken? current = await db.Set<RefreshToken>()
            .FromSql($"SELECT * FROM identity.refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

        if (current is null)
        {
            return Error.Unauthorized("auth.refresh_invalid", "Phiên đăng nhập không hợp lệ. Vui lòng đăng nhập lại.");
        }

        if (current.RevokedAt is not null)
        {
            if (current.RevokedReason == RefreshTokenRevokedReason.Rotated
                && now - current.RevokedAt.Value <= TimeSpan.FromSeconds(_options.ReuseGraceSeconds))
            {
                // Hai tab cùng refresh: tab chậm hơn đọc lại cookie mới (đã được tab kia ghi) rồi thử lại.
                return Error.Conflict("auth.refresh_conflict", "Phiên vừa được làm mới ở nơi khác — thử lại.");
            }

            if (current.RevokedReason == RefreshTokenRevokedReason.Rotated)
            {
                // Token đã thay thế bị dùng lại sau thời gian ân hạn ⇒ có hai bên giữ token ⇒ thu hồi cả họ.
                int revoked = await db.Set<RefreshToken>()
                    .Where(t => t.FamilyId == current.FamilyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.RevokedAt, now)
                        .SetProperty(t => t.RevokedReason, RefreshTokenRevokedReason.ReuseDetected), ct);
                await tx.CommitAsync(ct);
                logger.LogWarning("Refresh token reuse detected: user {UserId}, family {FamilyId}, revoked {Count} token(s)",
                    current.UserId, current.FamilyId, revoked);
                return Error.Unauthorized("auth.refresh_reused", "Phiên đăng nhập đã bị thu hồi vì lý do an toàn. Vui lòng đăng nhập lại.");
            }

            return Error.Unauthorized("auth.refresh_invalid", "Phiên đăng nhập đã kết thúc. Vui lòng đăng nhập lại.");
        }

        if (current.ExpiresAt <= now || current.SessionExpiresAt <= now)
        {
            return Error.Unauthorized("auth.session_expired", "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
        }

        User? user = await db.Set<User>().SingleOrDefaultAsync(u => u.Id == current.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            current.Revoke(RefreshTokenRevokedReason.Admin, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Error.Unauthorized("auth.account_disabled", "Tài khoản đã bị khoá.");
        }

        (RefreshToken next, string raw) = NewToken(user.Id, current.FamilyId, now, current.SessionExpiresAt, client);
        if (next.ExpiresAt <= now)
        {
            return Error.Unauthorized("auth.session_expired", "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
        }

        db.Add(next);
        current.Revoke(RefreshTokenRevokedReason.Rotated, now);
        current.ReplacedById = next.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Issue(user, next, raw);
    }

    /// <summary>Đăng xuất thiết bị hiện tại. Token lạ/đã thu hồi: bỏ qua (đăng xuất luôn “thành công”).</summary>
    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        byte[] hash = Hash(rawToken);
        DateTimeOffset now = time.GetUtcNow();
        await db.Set<RefreshToken>()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, RefreshTokenRevokedReason.Logout), ct);
    }

    /// <summary>Thu hồi mọi phiên của một tài khoản (đăng xuất mọi nơi, đổi mật khẩu).</summary>
    public Task<int> RevokeAllAsync(Guid userId, RefreshTokenRevokedReason reason, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        return db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, reason), ct);
    }

    private (RefreshToken Token, string Raw) NewToken(Guid userId, Guid familyId, DateTimeOffset now, DateTimeOffset sessionExpiresAt, ClientInfo client)
    {
        string raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset sliding = now.AddDays(_options.RefreshTokenDays);
        var token = new RefreshToken(userId, Hash(raw), familyId, now,
            sliding < sessionExpiresAt ? sliding : sessionExpiresAt, sessionExpiresAt)
        {
            CreatedIp = client.Ip,
            UserAgent = client.UserAgent,
        };
        return (token, raw);
    }

    private IssuedSession Issue(User user, RefreshToken token, string raw)
    {
        (string access, DateTimeOffset accessExpires) = accessTokens.Create(user);
        return new IssuedSession(user, access, accessExpires, raw, token.ExpiresAt);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
