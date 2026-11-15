using Microsoft.EntityFrameworkCore;

namespace server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
