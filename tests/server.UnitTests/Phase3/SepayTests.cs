using System.Text;
using server.Modules.Payments.Features.Sepay;

namespace server.UnitTests.Phase3;

public class SepaySignatureTests
{
    private const string Secret = "unit-test-secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 5, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":92704,"transferType":"in","transferAmount":2580000,"content":"DX7K3QH9M2"}""");
    private static string Ts(int offsetSeconds = 0) => (Now.ToUnixTimeSeconds() + offsetSeconds).ToString();

    [Fact]
    public void Valid_signature_passes()
        => Assert.Null(SepaySignature.Verify(Secret, SepaySignature.Sign(Secret, Ts(), Body), Ts(), Body, Now, 300));

    [Fact]
    public void Signature_is_lowercase_hex_with_sha256_prefix()
        => Assert.Matches("^sha256=[0-9a-f]{64}$", SepaySignature.Sign(Secret, Ts(), Body));

    [Fact]
    public void Changing_one_byte_of_the_body_fails()
    {
        string sig = SepaySignature.Sign(Secret, Ts(), Body);
        byte[] tampered = (byte[])Body.Clone();
        tampered[^3] ^= 0x01;                                    // 2580000 → số khác
        Assert.Equal("bad_signature", SepaySignature.Verify(Secret, sig, Ts(), tampered, Now, 300));
    }

    [Fact]
    public void Wrong_secret_fails()
        => Assert.Equal("bad_signature", SepaySignature.Verify(Secret, SepaySignature.Sign("other", Ts(), Body), Ts(), Body, Now, 300));

    [Fact]
    public void Replaying_an_old_request_fails_even_with_a_valid_signature()
    {
        string old = Ts(-301);
        Assert.Equal("stale_or_bad_timestamp", SepaySignature.Verify(Secret, SepaySignature.Sign(Secret, old, Body), old, Body, Now, 300));
    }

    [Fact]
    public void Timestamp_is_part_of_the_signed_message()
    {
        string sig = SepaySignature.Sign(Secret, Ts(), Body);
        Assert.Equal("bad_signature", SepaySignature.Verify(Secret, sig, Ts(10), Body, Now, 300));
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("sha256=abc", null)]
    [InlineData("sha256=abc", "khong-phai-so")]
    public void Missing_or_malformed_headers_fail(string? sig, string? ts)
        => Assert.NotNull(SepaySignature.Verify(Secret, sig, ts, Body, Now, 300));
}

public class SepayPayloadTests
{
    [Fact]
    public void Parses_the_documented_sepay_payload()
    {
        SepayPayload p = SepayPayload.TryParse("""
            {"id":92704,"gateway":"Vietcombank","transactionDate":"2026-11-27 12:02:37","accountNumber":"0011001234567",
             "code":null,"content":"MBVCB.123 DX7K3QH9M2 chuyen tien","transferType":"in","transferAmount":2580000,
             "accumulated":19077000,"subAccount":null,"referenceCode":"MBVCB.3278907687","description":""}
            """)!;
        Assert.Equal("92704", p.Id);
        Assert.True(p.IsIncoming);
        Assert.Equal(2_580_000, p.AmountVnd);
        Assert.Contains("DX7K3QH9M2", p.Content);
        Assert.Equal("MBVCB.3278907687", p.ReferenceCode);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 5, 2, 37, TimeSpan.Zero), p.TransactionDate);   // 12:02 giờ VN = 05:02 UTC
        Assert.Equal(TimeSpan.Zero, p.TransactionDate!.Value.Offset);                                   // lưu được vào timestamptz
    }

    [Fact]
    public void Outgoing_transfers_are_flagged()
        => Assert.False(SepayPayload.TryParse("""{"id":"x1","transferType":"out","transferAmount":100}""")!.IsIncoming);

    [Fact]
    public void Separate_code_field_is_searched_too()
        => Assert.StartsWith("DX7K3QH9M2", SepayPayload.TryParse("""{"id":1,"code":"DX7K3QH9M2","content":"thanh toan","transferAmount":1}""")!.Content);

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"transferAmount":1}""")]
    [InlineData("""khong phai json""")]
    public void Unusable_bodies_return_null(string json) => Assert.Null(SepayPayload.TryParse(json));
}
