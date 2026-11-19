namespace server.UnitTests.TestDoubles;

/// <summary>Đồng hồ điều khiển được cho test (thay cho TimeProvider.System).</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
