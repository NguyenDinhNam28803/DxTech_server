using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Domain;
using server.Shared.Results;

namespace server.Modules.Identity.Features.Admin;

/// <summary>Tạo tài khoản admin (ADR-012 §5). Chỉ gọi từ lệnh <c>--create-admin</c>, không có endpoint HTTP.</summary>
public sealed record CreateAdminCommand(string Email, string FullName, string Password);

public sealed record CreateAdminResponse(Guid Id, string Email);

/// <summary>
/// Không có mật khẩu mặc định, không tự nâng quyền tài khoản đã tồn tại: email đã có (dù là customer) ⇒ lỗi.
/// Đổi role tài khoản có sẵn là chức năng admin ở Phase 4 (có audit).
/// </summary>
public sealed class CreateAdminHandler
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;

    internal CreateAdminHandler(AppDbContext db, IPasswordHasher<User> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<Result<CreateAdminResponse>> HandleAsync(CreateAdminCommand command, CancellationToken ct = default)
    {
        var fieldErrors = new Dictionary<string, string[]>();

        string? email = EmailAddress.Normalize(command.Email);
        if (email is null)
        {
            fieldErrors["email"] = ["Email không hợp lệ."];
        }

        string fullName = (command.FullName ?? string.Empty).Trim();
        if (fullName.Length is < 1 or > 100)
        {
            fieldErrors["fullName"] = ["Họ tên phải có 1–100 ký tự."];
        }

        IReadOnlyList<string> passwordErrors = PasswordPolicy.Validate(command.Password, email);
        if (passwordErrors.Count > 0)
        {
            fieldErrors["password"] = [.. passwordErrors];
        }

        if (fieldErrors.Count > 0)
        {
            return Error.Validation("identity.invalid_admin", "Thông tin tài khoản admin không hợp lệ.", fieldErrors);
        }

        if (await _db.Set<User>().AnyAsync(u => u.Email == email, ct))
        {
            return Error.Conflict("identity.email_taken", $"Email {email} đã có tài khoản — không tự nâng quyền tài khoản có sẵn.");
        }

        var user = new User(email!, fullName, passwordHash: string.Empty, UserRole.Admin);
        user.PasswordHash = _hasher.HashPassword(user, command.Password);
        user.EmailConfirmedAt = null;

        _db.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Hai lệnh chạy đồng thời với cùng email: UNIQUE trong DB là chốt cuối.
            return Error.Conflict("identity.email_taken", $"Email {email} đã có tài khoản.");
        }

        return new CreateAdminResponse(user.Id, user.Email);
    }
}
