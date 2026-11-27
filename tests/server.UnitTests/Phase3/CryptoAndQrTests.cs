using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using server.Modules.Inventory.Crypto;
using server.Modules.Payments.Features;

namespace server.UnitTests.Phase3;

public class LicenseKeyProtectorTests
{
    private static readonly string K1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string K2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Hmac = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static LicenseKeyProtector Protector(int current = 1, bool withV2 = false)
    {
        var o = new LicenseOptions { CurrentVersion = current, HmacKey = Hmac, EncryptionKeys = { ["1"] = K1 } };
        if (withV2)
        {
            o.EncryptionKeys["2"] = K2;
        }

        return new LicenseKeyProtector(Options.Create(o));
    }

    [Fact]
    public void Encrypt_then_decrypt_returns_the_original_key()
    {
        LicenseKeyProtector p = Protector();
        var variant = Guid.NewGuid();
        byte[] c = p.Encrypt(variant, "  ABCDE-12345-FGHIJ  ");
        Assert.Equal("ABCDE-12345-FGHIJ", p.Decrypt(variant, 1, c));
        Assert.Equal(12 + 17 + 16, c.Length);
    }

    [Fact]
    public void Same_key_twice_gives_different_ciphertext_but_same_hash()
    {
        LicenseKeyProtector p = Protector();
        var variant = Guid.NewGuid();
        Assert.NotEqual(p.Encrypt(variant, "KEY-12345"), p.Encrypt(variant, "KEY-12345"));
        Assert.Equal(p.Hash("KEY-12345"), p.Hash("key 12345"));   // chuẩn hoá: hoa/thường, khoảng trắng, gạch
        Assert.NotEqual(p.Hash("KEY-12345"), p.Hash("KEY-12346"));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    public void Any_tampered_byte_fails_decryption(string part)
    {
        LicenseKeyProtector p = Protector();
        var variant = Guid.NewGuid();
        byte[] c = p.Encrypt(variant, "ABCDE-12345");
        int index = part switch { "nonce" => 0, "ciphertext" => 12, _ => c.Length - 1 };
        c[index] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => p.Decrypt(variant, 1, c));
    }

    [Fact]
    public void Ciphertext_copied_to_another_variant_cannot_be_decrypted()
    {
        LicenseKeyProtector p = Protector();
        byte[] c = p.Encrypt(Guid.NewGuid(), "ABCDE-12345");
        Assert.ThrowsAny<CryptographicException>(() => p.Decrypt(Guid.NewGuid(), 1, c));
    }

    [Fact]
    public void Key_rotation_keeps_old_keys_readable()
    {
        var variant = Guid.NewGuid();
        byte[] old = Protector(current: 1).Encrypt(variant, "OLD-KEY-1");

        LicenseKeyProtector rotated = Protector(current: 2, withV2: true);
        byte[] fresh = rotated.Encrypt(variant, "NEW-KEY-2");
        Assert.Equal(2, rotated.CurrentVersion);
        Assert.Equal("OLD-KEY-1", rotated.Decrypt(variant, 1, old));
        Assert.Equal("NEW-KEY-2", rotated.Decrypt(variant, 2, fresh));
        // Đánh tráo phiên bản trong DB cũng không giải mã được (version nằm trong AAD).
        Assert.ThrowsAny<CryptographicException>(() => rotated.Decrypt(variant, 2, old));
    }

    [Theory]
    [InlineData("abcd-efgh-1234", "1234")]
    [InlineData("XY 9", "XY9")]
    public void Hint_is_the_last_four_normalized_characters(string key, string hint) => Assert.Equal(hint, LicenseKeyProtector.Hint(key));

    [Fact]
    public void Options_validation_rejects_missing_or_short_keys()
    {
        static List<string> Errors(LicenseOptions o) => o.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(o)).Select(r => r.ErrorMessage!).ToList();

        Assert.NotEmpty(Errors(new LicenseOptions()));                                                              // thiếu hết
        Assert.NotEmpty(Errors(new LicenseOptions { HmacKey = Hmac, EncryptionKeys = { ["1"] = Convert.ToBase64String(new byte[16]) } }));  // 16 byte
        Assert.NotEmpty(Errors(new LicenseOptions { HmacKey = K1, EncryptionKeys = { ["1"] = K1 } }));               // HMAC trùng khoá mã hoá
        Assert.NotEmpty(Errors(new LicenseOptions { CurrentVersion = 2, HmacKey = Hmac, EncryptionKeys = { ["1"] = K1 } }));  // thiếu khoá phiên bản hiện tại
        Assert.Empty(Errors(new LicenseOptions { HmacKey = Hmac, EncryptionKeys = { ["1"] = K1 } }));
    }
}

public class VietQrTests
{
    [Fact]
    public void Crc16_matches_the_ccitt_false_check_value() => Assert.Equal(0x29B1, VietQr.Crc16("123456789"));

    [Fact]
    public void Payload_has_the_napas_fields_and_a_valid_checksum()
    {
        string qr = VietQr.Build("970436", "0011001234567", 2_580_000, "DX7K3QH9M2");

        Assert.StartsWith("000201" + "010212", qr);                       // định dạng + mã động
        Assert.Contains("0010A000000727", qr);                            // GUID NAPAS
        Assert.Contains("0006970436" + "01130011001234567", qr);          // BIN + số tài khoản
        Assert.Contains("0208QRIBFTTA", qr);                              // chuyển tới tài khoản
        Assert.Contains("5303704", qr);                                    // VND
        Assert.Contains("54072580000", qr);                                // số tiền
        Assert.Contains("5802VN", qr);
        Assert.Contains("62140810DX7K3QH9M2", qr);                         // nội dung = mã đơn
        Assert.Equal("6304", qr[^8..^4]);
        Assert.Equal(VietQr.Crc16(qr[..^4]).ToString("X4"), qr[^4..]);
    }

    [Fact]
    public void Non_positive_amount_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => VietQr.Build("970436", "0011001234567", 0, "DX7K3QH9M2"));
}
