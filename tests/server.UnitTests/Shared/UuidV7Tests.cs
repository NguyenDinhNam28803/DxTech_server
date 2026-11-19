using server.Shared.Ids;

namespace server.UnitTests.Shared;

public class UuidV7Tests
{
    [Fact]
    public void Has_version_7_and_rfc_variant()
    {
        Guid id = UuidV7.NewGuid();
        string text = id.ToString(); // xxxxxxxx-xxxx-7xxx-[89ab]xxx-xxxxxxxxxxxx

        Assert.Equal(7, UuidV7.GetVersion(id));
        Assert.Equal('7', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void Encodes_the_timestamp_in_milliseconds()
    {
        var at = new DateTimeOffset(2026, 11, 19, 3, 15, 42, 123, TimeSpan.Zero);

        Guid id = UuidV7.NewGuid(at);

        Assert.Equal(at, UuidV7.GetTimestamp(id));
        // 48 bit đầu (12 ký tự hex) chính là Unix ms → chuỗi UUID bắt đầu bằng thời điểm.
        Assert.StartsWith(at.ToUnixTimeMilliseconds().ToString("x12")[..8], id.ToString());
    }

    [Fact]
    public void Ids_from_later_milliseconds_sort_after_earlier_ones_in_string_and_byte_order()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<Guid> ids = Enumerable.Range(0, 200).Select(i => UuidV7.NewGuid(start.AddMilliseconds(i))).ToList();

        // PostgreSQL so sánh uuid theo thứ tự byte RFC = thứ tự chuỗi hex.
        List<string> asText = ids.Select(g => g.ToString()).ToList();
        Assert.Equal(asText.OrderBy(s => s, StringComparer.Ordinal), asText);
    }

    [Fact]
    public void Is_unique_within_the_same_millisecond()
    {
        var at = DateTimeOffset.UtcNow;
        var ids = new HashSet<Guid>();
        for (int i = 0; i < 10_000; i++)
        {
            Assert.True(ids.Add(UuidV7.NewGuid(at)));
        }
    }

    [Fact]
    public void Uses_the_injected_time_provider()
    {
        var clock = new TestDoubles.ManualTimeProvider(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero));

        Assert.Equal(clock.GetUtcNow(), UuidV7.GetTimestamp(UuidV7.NewGuid(clock)));
    }

    [Fact]
    public void Rejects_non_v7_ids()
    {
        Assert.Throws<ArgumentException>(() => UuidV7.GetTimestamp(Guid.NewGuid()));
    }
}
