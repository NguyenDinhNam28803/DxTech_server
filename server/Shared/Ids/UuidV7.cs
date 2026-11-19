using System.Buffers.Binary;
using System.Security.Cryptography;

namespace server.Shared.Ids;

/// <summary>
/// Sinh UUID phiên bản 7 theo RFC 9562 (ADR-006): 48 bit đầu là thời điểm Unix tính bằng mili-giây,
/// phần còn lại ngẫu nhiên. Vì tăng dần theo thời gian nên index B-tree ít phân mảnh hơn UUID v4,
/// mà vẫn không lộ số lượng bản ghi như khoá số tự tăng.
/// .NET 8 chưa có <c>Guid.CreateVersion7()</c> (có từ .NET 9) nên tự cài đặt.
/// </summary>
public static class UuidV7
{
    public static Guid NewGuid() => NewGuid(TimeProvider.System);

    public static Guid NewGuid(TimeProvider timeProvider) => NewGuid(timeProvider.GetUtcNow());

    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        long unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0 || unixMs > 0xFFFF_FFFF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Thời điểm nằm ngoài phạm vi 48 bit của UUID v7.");
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[6..]);

        // Byte 0–5: thời điểm (big-endian).
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // Byte 6: 4 bit cao là version = 7.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        // Byte 8: 2 bit cao là variant = 10 (RFC 4122/9562).
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        // bigEndian: true — giữ đúng thứ tự byte của RFC; Npgsql ghi Guid xuống PostgreSQL theo thứ tự này,
        // nên PostgreSQL sắp xếp uuid đúng theo thời gian.
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Đọc lại thời điểm tạo từ một UUID v7 (dùng cho debug/test).</summary>
    public static DateTimeOffset GetTimestamp(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!id.TryWriteBytes(bytes, bigEndian: true, out _) || GetVersion(id) != 7)
        {
            throw new ArgumentException("Không phải UUID v7.", nameof(id));
        }

        long unixMs = (long)(BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16);
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    /// <summary>Version của UUID (4 bit cao của byte 6 theo thứ tự RFC).</summary>
    public static int GetVersion(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[6] >> 4;
    }
}
