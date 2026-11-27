using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Domain;
using server.Shared.Results;

namespace server.Modules.Identity.Features.Admin;

/// <summary>Đổi role một tài khoản có sẵn (ADR-015 §4). Chỉ gọi từ lệnh <c>--set-role</c>, không có endpoint HTTP.</summary>
public sealed record SetRoleCommand(string Email, string Role);

public sealed record SetRoleResponse(Guid Id, string Email, string PreviousRole, string Role, bool Changed);

/// <summary>
/// Không tạo tài khoản (người đó phải tự đăng ký trước — mật khẩu chỉ họ biết). Đổi role ⇒ xoay security stamp:
/// access token cũ (mang role cũ) bị policy admin/content từ chối ngay ở lần kiểm tra DB kế tiếp; lần refresh sau
/// nhận token mang role mới. Phiên đăng nhập (refresh token) giữ nguyên — không bắt đăng nhập lại.
/// Hạ admin cuối cùng đang hoạt động bị chặn (tránh tự khoá hệ thống).
/// </summary>
public sealed class SetRoleHandler
{
    private readonly AppDbContext _db;

    internal SetRoleHandler(AppDbContext db) => _db = db;

    public async Task<Result<SetRoleResponse>> HandleAsync(SetRoleCommand command, CancellationToken ct = default)
    {
        string? email = EmailAddress.Normalize(command.Email);
        if (email is null)
        {
            return Error.Validation("identity.invalid_email", "Email không hợp lệ.");
        }

        if (!UserRoles.TryParse(command.Role, out UserRole role))
        {
            string allowed = string.Join(" | ", Enum.GetValues<UserRole>().Select(UserRoles.Name));
            return Error.Validation("identity.invalid_role", $"Role không hợp lệ — dùng một trong: {allowed}.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Tuần tự hoá mọi lệnh đổi role: hai lệnh hạ hai admin cuối cùng cùng lúc không thể cùng thấy "còn admin khác".
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('identity.set_role'))", ct);
        // Ghi đồng thời từ luồng khác (đăng nhập sai, đổi mật khẩu) bị xmin chặn ⇒ DbUpdateConcurrencyException, không ghi đè lặng lẽ.
        User? user = await _db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            return Error.NotFound("identity.user_not_found", $"Không có tài khoản {email} — người dùng cần tự đăng ký trước.");
        }

        string previous = UserRoles.Name(user.Role);
        if (user.Role == role)
        {
            return new SetRoleResponse(user.Id, user.Email, previous, previous, Changed: false);
        }

        if (user.Role == UserRole.Admin)
        {
            bool otherActiveAdmin = await _db.Set<User>()
                .AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && u.Status == UserStatus.Active, ct);
            if (!otherActiveAdmin)
            {
                return Error.Conflict("identity.last_admin", "Đây là admin đang hoạt động cuối cùng — tạo admin khác trước khi hạ quyền.");
            }
        }

        user.Role = role;
        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SetRoleResponse(user.Id, user.Email, previous, UserRoles.Name(role), Changed: true);
    }
}
