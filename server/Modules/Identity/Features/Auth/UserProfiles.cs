using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Contracts;
using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Features.Auth;

internal sealed class UserProfiles(AppDbContext db) : IUserProfiles
{
    public async Task<UserProfileRef?> GetAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Set<User>().AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.Id, x.Email, x.FullName, x.Status }).SingleOrDefaultAsync(ct);
        return u is null ? null : new UserProfileRef(u.Id, u.Email, u.FullName, u.Status == UserStatus.Active);
    }

    public async Task<UserProfileRef?> GetOldestActiveAdminAsync(CancellationToken ct)
    {
        var u = await db.Set<User>().AsNoTracking()
            .Where(x => x.Role == UserRole.Admin && x.Status == UserStatus.Active)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Email, x.FullName }).FirstOrDefaultAsync(ct);
        return u is null ? null : new UserProfileRef(u.Id, u.Email, u.FullName, IsActive: true);
    }
}
