using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Infrastructure.Options;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Auth;
using server.Modules.Identity.Domain;
using server.Shared.Results;

namespace server.Modules.Identity.Features.Auth;

/// <summary>Đăng ký tài khoản khách (ADR-012 §3, §6). Luôn là customer; cấp phiên ngay.</summary>
public sealed class RegisterHandler
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly SessionService _sessions;

    internal RegisterHandler(AppDbContext db, IPasswordHasher<User> hasher, SessionService sessions)
        => (_db, _hasher, _sessions) = (db, hasher, sessions);

    internal async Task<Result<AuthResult>> HandleAsync(RegisterRequest request, ClientInfo client, CancellationToken ct)
    {
        var fieldErrors = new Dictionary<string, string[]>();
        string? email = EmailAddress.Normalize(request.Email);
        if (email is null)
        {
            fieldErrors["email"] = ["Email không hợp lệ."];
        }

        string fullName = (request.FullName ?? string.Empty).Trim();
        if (fullName.Length is < 1 or > 100)
        {
            fieldErrors["fullName"] = ["Họ tên phải có 1–100 ký tự."];
        }

        IReadOnlyList<string> passwordErrors = PasswordPolicy.Validate(request.Password, email);
        if (passwordErrors.Count > 0)
        {
            fieldErrors["password"] = [.. passwordErrors];
        }

        if (fieldErrors.Count > 0)
        {
            return Error.Validation("auth.invalid_registration", "Thông tin đăng ký không hợp lệ.", fieldErrors);
        }

        // Chấp nhận lộ “email đã có tài khoản” vì chưa có email xác nhận (ADR-012 §3); rate limit giảm dò hàng loạt.
        if (await _db.Set<User>().AnyAsync(u => u.Email == email, ct))
        {
            return Error.Conflict("auth.email_taken", "Email này đã có tài khoản. Hãy đăng nhập hoặc dùng email khác.");
        }

        var user = new User(email!, fullName, passwordHash: string.Empty, UserRole.Customer);
        user.PasswordHash = _hasher.HashPassword(user, request.Password!);
        _db.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("auth.email_taken", "Email này đã có tài khoản. Hãy đăng nhập hoặc dùng email khác.");
        }

        return AuthResult.From(await _sessions.StartAsync(user, client, ct));
    }
}

/// <summary>
/// Đăng nhập (ADR-012 §3). Sai email hay sai mật khẩu đều trả cùng một lỗi, cùng thời gian xử lý.
/// Bộ đếm sai/khoá tạm cập nhật bằng UPDATE nguyên tử — nhiều request đồng thời không làm mất lần đếm.
/// </summary>
public sealed class LoginHandler
{
    private static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "Email hoặc mật khẩu không đúng.");

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly SessionService _sessions;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly Lazy<(User User, string Hash)> _dummy;

    internal LoginHandler(AppDbContext db, IPasswordHasher<User> hasher, SessionService sessions, IOptions<AuthOptions> options, TimeProvider time)
    {
        (_db, _hasher, _sessions, _options, _time) = (db, hasher, sessions, options.Value, time);
        _dummy = new(() =>
        {
            var u = new User("dummy@invalid.local", "x", "x", UserRole.Customer);
            return (u, _hasher.HashPassword(u, Guid.NewGuid().ToString()));
        });
    }

    internal async Task<Result<AuthResult>> HandleAsync(LoginRequest request, ClientInfo client, CancellationToken ct)
    {
        string? email = EmailAddress.Normalize(request.Email);
        string password = request.Password ?? string.Empty;
        DateTimeOffset now = _time.GetUtcNow();

        User? user = email is null ? null : await _db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            // Tốn thời gian như một lần kiểm tra thật ⇒ không dò được email tồn tại qua thời gian phản hồi.
            _hasher.VerifyHashedPassword(_dummy.Value.User, _dummy.Value.Hash, password);
            return InvalidCredentials;
        }

        if (user.LockoutUntil > now)
        {
            return Error.Locked("auth.locked",
                $"Tài khoản tạm khoá do đăng nhập sai nhiều lần. Thử lại sau {user.LockoutUntil.Value.ToOffset(TimeSpan.FromHours(7)):HH:mm}.");
        }

        PasswordVerificationResult verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
        {
            int max = _options.MaxFailedLogins;
            DateTimeOffset? lockUntil = now.AddMinutes(_options.LockoutMinutes);
            await _db.Set<User>().Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LockoutUntil, u => u.FailedLoginCount + 1 >= max ? lockUntil : u.LockoutUntil)
                .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= max ? 0 : u.FailedLoginCount + 1)
                .SetProperty(u => u.UpdatedAt, now), ct);
            return InvalidCredentials;
        }

        if (user.Status != UserStatus.Active)
        {
            return Error.Forbidden("auth.account_disabled", "Tài khoản đã bị khoá. Liên hệ hỗ trợ.");
        }

        string hash = verify == PasswordVerificationResult.SuccessRehashNeeded
            ? _hasher.HashPassword(user, password)
            : user.PasswordHash;
        await _db.Set<User>().Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.LockoutUntil, (DateTimeOffset?)null)
            .SetProperty(u => u.LastLoginAt, now)
            .SetProperty(u => u.PasswordHash, hash)
            .SetProperty(u => u.UpdatedAt, now), ct);

        return AuthResult.From(await _sessions.StartAsync(user, client, ct));
    }
}

/// <summary>Làm mới phiên bằng refresh token trong cookie; đăng xuất thiết bị hiện tại.</summary>
public sealed class SessionHandler
{
    private readonly SessionService _sessions;

    internal SessionHandler(SessionService sessions) => _sessions = sessions;

    internal async Task<Result<AuthResult>> RefreshAsync(string? rawToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return Error.Unauthorized("auth.refresh_missing", "Chưa đăng nhập.");
        }

        Result<IssuedSession> result = await _sessions.RotateAsync(rawToken, client, ct);
        return result.IsSuccess ? AuthResult.From(result.Value) : result.Error;
    }

    internal Task LogoutAsync(string? rawToken, CancellationToken ct)
        => string.IsNullOrEmpty(rawToken) ? Task.CompletedTask : _sessions.RevokeAsync(rawToken, ct);
}

/// <summary>Tài khoản của người đang đăng nhập: xem hồ sơ, đổi mật khẩu, đăng xuất mọi nơi.</summary>
public sealed class AccountHandler
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly SessionService _sessions;

    internal AccountHandler(AppDbContext db, IPasswordHasher<User> hasher, SessionService sessions)
        => (_db, _hasher, _sessions) = (db, hasher, sessions);

    internal async Task<Result<UserProfile>> GetAsync(Guid userId, CancellationToken ct)
    {
        User? user = await _db.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is { Status: UserStatus.Active }
            ? UserProfile.From(user)
            : Error.Unauthorized("auth.account_disabled", "Tài khoản không còn hoạt động.");
    }

    /// <summary>Đổi mật khẩu ⇒ đổi security stamp, thu hồi mọi phiên, cấp phiên mới cho thiết bị đang dùng.</summary>
    internal async Task<Result<AuthResult>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, ClientInfo client, CancellationToken ct)
    {
        User? user = await _db.Set<User>().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not { Status: UserStatus.Active })
        {
            return Error.Unauthorized("auth.account_disabled", "Tài khoản không còn hoạt động.");
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return Error.Validation("auth.wrong_password", "Mật khẩu hiện tại không đúng.",
                new Dictionary<string, string[]> { ["currentPassword"] = ["Mật khẩu hiện tại không đúng."] });
        }

        IReadOnlyList<string> errors = PasswordPolicy.Validate(request.NewPassword, user.Email);
        if (errors.Count > 0)
        {
            return Error.Validation("auth.weak_password", "Mật khẩu mới không hợp lệ.",
                new Dictionary<string, string[]> { ["newPassword"] = [.. errors] });
        }

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword!);
        user.RotateSecurityStamp();
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("auth.concurrent_update", "Tài khoản vừa được cập nhật ở nơi khác — thử lại.");
        }

        await _sessions.RevokeAllAsync(userId, RefreshTokenRevokedReason.PasswordChanged, ct);
        return AuthResult.From(await _sessions.StartAsync(user, client, ct));
    }

    /// <summary>Đăng xuất mọi thiết bị: đổi security stamp (admin mất quyền ngay) + thu hồi mọi refresh token.</summary>
    internal async Task<Result> LogoutAllAsync(Guid userId, CancellationToken ct)
    {
        User? user = await _db.Set<User>().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.Unauthorized("auth.account_disabled", "Tài khoản không còn hoạt động.");
        }

        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(ct);
        await _sessions.RevokeAllAsync(userId, RefreshTokenRevokedReason.Logout, ct);
        return Result.Success();
    }
}
