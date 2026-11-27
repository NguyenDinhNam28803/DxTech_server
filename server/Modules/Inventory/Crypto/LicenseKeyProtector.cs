using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace server.Modules.Inventory.Crypto;

/// <summary>
/// Mã hoá / giải mã / băm license key theo ADR-014. Chỉ module Inventory dùng; key rõ không bao giờ ghi log.
/// <list type="bullet">
/// <item>AES-256-GCM, nonce 12 byte ngẫu nhiên mỗi key, tag 16 byte; lưu <c>nonce ‖ ciphertext ‖ tag</c>.</item>
/// <item>AAD = <c>variant_id (16 byte) ‖ key_version (2 byte)</c>: chép bản mã sang gói khác / đổi phiên bản ⇒ giải mã thất bại.</item>
/// <item><c>key_hash</c> = HMAC-SHA256(khoá HMAC riêng, key đã chuẩn hoá) — chống nhập trùng mà không giải mã cả kho;
/// HMAC (không phải SHA thường) để người có bản dump không thử ngược được key ngắn.</item>
/// </list>
/// </summary>
internal sealed class LicenseKeyProtector
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    private readonly Dictionary<short, byte[]> _keys;
    private readonly byte[] _hmacKey;

    public LicenseKeyProtector(IOptions<LicenseOptions> options)
    {
        LicenseOptions o = options.Value;
        CurrentVersion = (short)o.CurrentVersion;
        _keys = o.EncryptionKeys
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => short.Parse(p.Key), p => Convert.FromBase64String(p.Value.Trim()));
        _hmacKey = Convert.FromBase64String(o.HmacKey.Trim());
    }

    public short CurrentVersion { get; }

    /// <summary>Bỏ khoảng trắng và dấu gạch, đổi chữ hoa: "abcd-efgh 1234" và "ABCDEFGH1234" là một key.</summary>
    public static string Normalize(string key)
        => new(key.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>4 ký tự cuối của key đã chuẩn hoá — admin nhận diện mà không cần giải mã.</summary>
    public static string Hint(string key)
    {
        string n = Normalize(key);
        return n.Length <= 4 ? n : n[^4..];
    }

    public byte[] Hash(string key) => HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(Normalize(key)));

    /// <summary>Mã hoá key (giữ nguyên định dạng gốc để giao cho khách đúng như nhà phát hành cấp) bằng khoá hiện tại.</summary>
    public byte[] Encrypt(Guid variantId, string key)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(key.Trim());
        byte[] output = new byte[NonceBytes + plaintext.Length + TagBytes];
        Span<byte> nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(KeyFor(CurrentVersion), TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceBytes, plaintext.Length), output.AsSpan(NonceBytes + plaintext.Length), Aad(variantId, CurrentVersion));
        CryptographicOperations.ZeroMemory(plaintext);
        return output;
    }

    /// <summary>Giải mã. Bản mã bị sửa, sai gói hoặc sai phiên bản ⇒ <see cref="CryptographicException"/>.</summary>
    public string Decrypt(Guid variantId, short version, byte[] encrypted)
    {
        if (encrypted.Length < NonceBytes + TagBytes + 1)
        {
            throw new CryptographicException("Bản mã quá ngắn.");
        }

        int length = encrypted.Length - NonceBytes - TagBytes;
        byte[] plaintext = new byte[length];
        using var aes = new AesGcm(KeyFor(version), TagBytes);
        aes.Decrypt(encrypted.AsSpan(0, NonceBytes), encrypted.AsSpan(NonceBytes, length), encrypted.AsSpan(NonceBytes + length), plaintext, Aad(variantId, version));
        string key = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return key;
    }

    private byte[] KeyFor(short version)
        => _keys.TryGetValue(version, out byte[]? key)
            ? key
            : throw new CryptographicException($"Không có khoá mã hoá phiên bản {version} (License:EncryptionKeys:{version}).");

    private static byte[] Aad(Guid variantId, short version)
    {
        byte[] aad = new byte[18];
        variantId.TryWriteBytes(aad.AsSpan(0, 16));
        BinaryPrimitives.WriteInt16BigEndian(aad.AsSpan(16), version);
        return aad;
    }
}
