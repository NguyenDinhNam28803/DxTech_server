using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence.Interceptors;
using server.Shared.Abstractions;
using server.UnitTests.TestDoubles;

namespace server.UnitTests.Infrastructure;

public class AuditableInterceptorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Added_entity_gets_created_and_updated_at_from_the_clock()
    {
        var clock = new ManualTimeProvider(Start);
        var interceptor = new AuditableInterceptor(clock);
        using var context = new AuditTestContext();
        var entity = new Note { Id = 1, Text = "new" };
        context.Add(entity);

        interceptor.ApplyTimestamps(context.ChangeTracker);

        Assert.Equal(Start, entity.CreatedAt);
        Assert.Equal(Start, entity.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, entity.CreatedAt.Offset); // luôn UTC → hợp lệ cho timestamptz
    }

    [Fact]
    public void Modified_entity_updates_only_updated_at_and_protects_created_at()
    {
        var clock = new ManualTimeProvider(Start);
        var interceptor = new AuditableInterceptor(clock);
        using var context = new AuditTestContext();
        var entity = new Note { Id = 1, Text = "old", CreatedAt = Start, UpdatedAt = Start };
        context.Attach(entity);

        clock.Advance(TimeSpan.FromMinutes(30));
        entity.Text = "edited";
        entity.CreatedAt = Start.AddYears(-1); // cố ý sửa sai — phải bị bỏ qua khi lưu
        context.ChangeTracker.DetectChanges();
        interceptor.ApplyTimestamps(context.ChangeTracker);

        var entry = context.Entry(entity);
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.Equal(Start.AddMinutes(30), entity.UpdatedAt);
        Assert.False(entry.Property(e => e.CreatedAt).IsModified);
    }

    [Fact]
    public void Unchanged_entity_is_left_alone()
    {
        var interceptor = new AuditableInterceptor(new ManualTimeProvider(Start));
        using var context = new AuditTestContext();
        var entity = new Note { Id = 1, CreatedAt = Start.AddDays(-2), UpdatedAt = Start.AddDays(-1) };
        context.Attach(entity);

        interceptor.ApplyTimestamps(context.ChangeTracker);

        Assert.Equal(Start.AddDays(-1), entity.UpdatedAt);
    }

    private sealed class Note : IAuditable
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>Chỉ dùng ChangeTracker, không kết nối database.</summary>
    private sealed class AuditTestContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=localhost;Database=tracker_only");

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>();
    }
}
