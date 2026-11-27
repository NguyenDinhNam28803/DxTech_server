using System.ComponentModel.DataAnnotations;

namespace server.Modules.Inventory.Crypto;

/// <summary>
/// Khoá mã hoá license key (ADR-014). Production đặt trong <c>.env</c>:
/// <c>LICENSE_ENC_KEY_V1</c>, <c>LICENSE_HMAC_KEY</c> (mỗi khoá 32 byte base64: <c>openssl rand -base64 32</c>).
/// Khoá KHÔNG nằm trong DB ⇒ backup DB không chứa khoá — phải cất riêng ở 2 nơi.
/// Thiếu / sai độ dài ⇒ API không khởi động (giống khoá ký JWT).
/// </summary>
internal sealed class LicenseOptions : IValidatableObject
{
    public const string SectionName = "License";
    public const int KeyBytes = 32;

    /// <summary>Phiên bản khoá dùng để mã hoá key MỚI. Key cũ giải mã bằng đúng phiên bản đã lưu trong <c>key_version</c>.</summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>Phiên bản → khoá AES-256 (base64). Cấu hình: <c>License:EncryptionKeys:1</c>, <c>License:EncryptionKeys:2</c>…</summary>
    public Dictionary<string, string> EncryptionKeys { get; set; } = [];

    /// <summary>Khoá HMAC cho <c>key_hash</c> (chống nhập trùng). Không xoay định kỳ (ADR-014).</summary>
    public string HmacKey { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CurrentVersion is < 1 or > short.MaxValue)
        {
            yield return new ValidationResult("License:CurrentVersion phải từ 1.", [nameof(CurrentVersion)]);
        }

        if (!EncryptionKeys.TryGetValue(CurrentVersion.ToString(), out string? current) || string.IsNullOrWhiteSpace(current))
        {
            yield return new ValidationResult(
                $"Thiếu License:EncryptionKeys:{CurrentVersion} (production: LICENSE_ENC_KEY_V{CurrentVersion} trong .env, tạo bằng: openssl rand -base64 32).",
                [nameof(EncryptionKeys)]);
        }

        foreach ((string version, string key) in EncryptionKeys)
        {
            if (!short.TryParse(version, out short v) || v < 1)
            {
                yield return new ValidationResult($"Phiên bản khoá '{version}' không hợp lệ.", [nameof(EncryptionKeys)]);
            }
            else if (!string.IsNullOrWhiteSpace(key) && !Is32Bytes(key))
            {
                yield return new ValidationResult($"License:EncryptionKeys:{version} phải là {KeyBytes} byte base64.", [nameof(EncryptionKeys)]);
            }
        }

        if (!Is32Bytes(HmacKey))
        {
            yield return new ValidationResult(
                "Thiếu hoặc sai License:HmacKey (production: LICENSE_HMAC_KEY trong .env, tạo bằng: openssl rand -base64 32).",
                [nameof(HmacKey)]);
        }
        else if (EncryptionKeys.Values.Any(k => k == HmacKey))
        {
            yield return new ValidationResult("License:HmacKey phải khác mọi khoá mã hoá.", [nameof(HmacKey)]);
        }
    }

    private static bool Is32Bytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(base64.Trim(), buffer, out int written) && written == KeyBytes;
    }
}
