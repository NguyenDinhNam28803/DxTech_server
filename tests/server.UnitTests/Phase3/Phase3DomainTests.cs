using System.Text.RegularExpressions;
using server.Modules.Inventory.Domain;
using server.Modules.Sales.Domain;
using server.Modules.Sales.Persistence;

namespace server.UnitTests.Phase3;

public class OrderCodeTests
{
    [Fact]
    public void Ten_thousand_codes_match_the_pattern_and_avoid_ambiguous_letters()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => OrderCode.New()).ToList();

        Assert.All(codes, c => Assert.Matches(OrderCode.Pattern, c));
        Assert.All(codes, c => Assert.DoesNotMatch("[ILOU]", c[2..]));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Characters_are_spread_evenly_over_the_alphabet()
    {
        var counts = new Dictionary<char, int>();
        for (int i = 0; i < 20_000; i++)
        {
            foreach (char c in OrderCode.New()[2..])
            {
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        double mean = 20_000.0 * OrderCode.RandomLength / OrderCode.Alphabet.Length;
        Assert.Equal(OrderCode.Alphabet.Length, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, mean * 0.85, mean * 1.15));
    }

    [Theory]
    [InlineData("DX7K3QH9M2", true)]
    [InlineData("dx7k3qh9m2", false)]   // lưu luôn chữ hoa
    [InlineData("DX-7K3QH9M2", false)]  // không có dấu gạch (ADR-013)
    [InlineData("DX7K3QH9MI", false)]   // I không thuộc Crockford base32
    [InlineData("DX7K3QH9M", false)]
    [InlineData(null, false)]
    public void IsValid(string? code, bool expected) => Assert.Equal(expected, OrderCode.IsValid(code));

    [Theory]
    [InlineData("DX7K3QH9M2", "DX7K3QH9M2")]
    [InlineData("thanh toan dx7k3qh9m2 cam on", "DX7K3QH9M2")]          // app ngân hàng hạ chữ thường
    [InlineData("MBVCB.123456.DX7K3-QH9M2.CT tu 0123", "DX7K3QH9M2")]  // chèn dấu gạch
    [InlineData("DX 7K3Q H9M2", "DX7K3QH9M2")]                           // khách gõ có khoảng trắng
    [InlineData("chuyen tien mua hang", null)]
    [InlineData("", null)]
    public void FindIn_transfer_content(string content, string? expected) => Assert.Equal(expected, OrderCode.FindIn(content));
}

public class OrderTests
{
    private static Order NewOrder() => new(Guid.NewGuid(), Guid.NewGuid(), "khach@dxtechs.test", "Khách", DateTimeOffset.UtcNow);

    [Fact]
    public void New_order_waits_for_payment_for_30_minutes()
    {
        Order o = NewOrder();
        Assert.Equal(OrderStatus.PendingPayment, o.Status);
        Assert.Equal(o.CreatedAt + TimeSpan.FromMinutes(30), o.ExpiresAt);
        Assert.True(OrderCode.IsValid(o.Code));
    }

    [Fact]
    public void Adding_items_recomputes_totals()
    {
        Order o = NewOrder();
        o.AddItem(Guid.NewGuid(), "Microsoft 365", "12 tháng", "M365-12M", ItemDeliveryType.LicenseKey, 1_290_000, 2);
        o.AddItem(Guid.NewGuid(), "Cài đặt", "1 máy", "SETUP-1", ItemDeliveryType.Manual, 200_000, 1);

        Assert.Equal(2_780_000, o.SubtotalVnd);
        Assert.Equal(0, o.DiscountVnd);
        Assert.Equal(2_780_000, o.TotalVnd);
        Assert.Equal(2_580_000, o.Items[0].LineTotalVnd);
    }

    [Fact]
    public void Same_variant_twice_is_rejected_quantities_must_be_merged()
    {
        Order o = NewOrder();
        var variant = Guid.NewGuid();
        o.AddItem(variant, "P", "V", "SKU-1", ItemDeliveryType.LicenseKey, 100, 1);
        Assert.Throws<InvalidOperationException>(() => o.AddItem(variant, "P", "V", "SKU-1", ItemDeliveryType.LicenseKey, 100, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Quantity_outside_1_to_20_is_rejected(int quantity)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NewOrder().AddItem(Guid.NewGuid(), "P", "V", "S", ItemDeliveryType.LicenseKey, 100, quantity));

    [Fact]
    public void Items_cannot_be_added_after_payment()
    {
        Order o = NewOrder();
        o.Status = OrderStatus.Paid;
        Assert.Throws<InvalidOperationException>(() => o.AddItem(Guid.NewGuid(), "P", "V", "S", ItemDeliveryType.LicenseKey, 100, 1));
    }
}

public class LicenseKeyTests
{
    private static LicenseKey NewKey()
        => new(Guid.NewGuid(), Guid.NewGuid(), new byte[40], 1, new byte[LicenseKey.HashLength], "AB12", DateTimeOffset.UtcNow);

    [Fact]
    public void Reserve_deliver_follows_the_state_machine()
    {
        LicenseKey k = NewKey();
        var item = Guid.NewGuid();
        k.Reserve(item, DateTimeOffset.UtcNow);
        Assert.Equal(LicenseKeyStatus.Reserved, k.Status);
        Assert.Equal(item, k.OrderItemId);

        k.Deliver(DateTimeOffset.UtcNow);
        Assert.Equal(LicenseKeyStatus.Delivered, k.Status);
        Assert.NotNull(k.DeliveredAt);
        Assert.Throws<InvalidOperationException>(() => k.Release());   // đã giao thì không trả về kho
    }

    [Fact]
    public void Release_returns_key_to_stock()
    {
        LicenseKey k = NewKey();
        k.Reserve(Guid.NewGuid(), DateTimeOffset.UtcNow);
        k.Release();
        Assert.Equal(LicenseKeyStatus.Available, k.Status);
        Assert.Null(k.OrderItemId);
        Assert.Null(k.ReservedAt);
    }

    [Fact]
    public void Available_key_cannot_be_delivered_directly()
        => Assert.Throws<InvalidOperationException>(() => NewKey().Deliver(DateTimeOffset.UtcNow));

    [Fact]
    public void Wrong_hash_or_ciphertext_length_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LicenseKey(Guid.NewGuid(), Guid.NewGuid(), new byte[40], 1, new byte[16], "AB12", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new LicenseKey(Guid.NewGuid(), Guid.NewGuid(), new byte[28], 1, new byte[32], "AB12", DateTimeOffset.UtcNow));
    }
}

public class Phase3PrivilegesTests
{
    /// <summary>
    /// roles.sql chạy lại mỗi lần deploy và cấp lại mọi quyền ⇒ mục 5b phải thu hồi ĐÚNG như migration.
    /// Hai bản lệch nhau ⇒ lần deploy thứ hai âm thầm trả lại quyền DELETE cho API.
    /// </summary>
    [Fact]
    public void Phase3_privileges_match_roles_sql()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "server.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Không tìm thấy thư mục gốc repo.");
        }

        string rolesSql = File.ReadAllText(Path.Combine(root, "deploy", "postgres", "roles.sql"));
        static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        Assert.Contains(Norm(Phase3Privileges.Sql), Norm(rolesSql));
    }
}
