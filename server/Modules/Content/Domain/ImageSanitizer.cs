using System.Buffers.Binary;
using System.Text;

namespace server.Modules.Content.Domain;

/// <summary>Ảnh đã kiểm: loại xác định từ nội dung file, kích thước, bytes đã bỏ metadata.</summary>
internal sealed record SanitizedImage(string ContentType, string Extension, int Width, int Height, byte[] Bytes, int RemovedBytes);

/// <summary>
/// Kiểm + làm sạch ảnh tải lên (ADR-015 §5) mà không giải mã ảnh, không thư viện ngoài:
/// <list type="bullet">
/// <item>Loại file xác định bằng <b>magic bytes</b> — không tin phần mở rộng hay Content-Type. Chỉ JPEG, PNG, WebP; SVG/GIF/HTML ⇒ từ chối.</item>
/// <item>Đọc cấu trúc (segment/chunk) đến hết, độ dài từng phần phải nằm trong file ⇒ file cắt cụt hoặc giả mạo bị từ chối.</item>
/// <item>Bỏ metadata theo <b>danh sách trắng</b>: chỉ giữ phần cần để hiển thị đúng. EXIF (có GPS, model máy, ngày giờ), XMP, IPTC,
///       comment, text chunk bị xoá. Hệ quả: mất thông tin xoay của ảnh điện thoại — ảnh cần được xoay đúng trước khi tải lên.</item>
/// </list>
/// </summary>
internal static class ImageSanitizer
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static (SanitizedImage? Image, string? Error) Sanitize(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return Jpeg(data);
            }

            if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            {
                return Png(data);
            }

            if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            {
                return WebP(data);
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return (null, "File ảnh bị hỏng hoặc cắt cụt.");
        }

        return (null, "Chỉ nhận ảnh JPEG, PNG hoặc WebP (kiểm theo nội dung file, không theo tên file).");
    }

    // ---------- JPEG: SOI, các segment FFxx có độ dài, tới SOS thì chép nguyên phần còn lại ----------

    private static (SanitizedImage?, string?) Jpeg(ReadOnlySpan<byte> d)
    {
        var output = new MemoryStream(d.Length);
        output.Write(d[..2]);
        int i = 2, width = 0, height = 0;
        bool sos = false;
        while (i < d.Length)
        {
            if (d[i] != 0xFF)
            {
                return (null, "JPEG không hợp lệ (segment hỏng).");
            }

            while (i < d.Length && d[i] == 0xFF)
            {
                i++; // byte đệm 0xFF
            }

            byte marker = d[i];
            i++;
            if (marker == 0xD9)
            {
                break; // EOI trước SOS ⇒ không có dữ liệu ảnh
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue; // marker không có độ dài
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i, 2));
            if (len < 2 || i + len > d.Length)
            {
                return (null, "JPEG không hợp lệ (độ dài segment vượt file).");
            }

            ReadOnlySpan<byte> segment = d.Slice(i - 2, len + 2); // FF marker + độ dài + dữ liệu
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
            }

            if (marker == 0xDA)
            {
                output.WriteByte(0xFF);
                output.Write(d[(i - 1)..]); // SOS + toàn bộ dữ liệu nén + EOI: không đụng tới
                sos = true;
                break;
            }

            if (KeepJpegSegment(marker, d.Slice(i + 2, len - 2)))
            {
                output.Write(segment);
            }

            i += len;
        }

        if (!sos || width == 0 || height == 0)
        {
            return (null, "JPEG không hợp lệ (thiếu khung ảnh).");
        }

        return (new SanitizedImage("image/jpeg", "jpg", width, height, output.ToArray(), d.Length - (int)output.Length), null);
    }

    /// <summary>Giữ: bảng lượng tử/Huffman/khung ảnh (không phải APPn/COM), APP0 JFIF, APP2 hồ sơ màu ICC, APP14 Adobe (cần để giải mã CMYK).</summary>
    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 => true,
        0xE2 => payload.StartsWith("ICC_PROFILE\0"u8),
        0xEE => true,
        >= 0xE1 and <= 0xEF => false, // EXIF/XMP (APP1), IPTC (APP13), các APPn khác
        0xFE => false,                // COM
        _ => true,
    };

    // ---------- PNG: chữ ký + chunk (độ dài, loại, dữ liệu, CRC) ----------

    private static readonly HashSet<string> PngKeep =
        ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "pHYs", "bKGD"];

    private static (SanitizedImage?, string?) Png(ReadOnlySpan<byte> d)
    {
        var output = new MemoryStream(d.Length);
        output.Write(d[..8]);
        int i = 8, width = 0, height = 0;
        bool end = false;
        while (i + 12 <= d.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i, 4));
            if (len > int.MaxValue || i + 12 + (long)len > d.Length)
            {
                return (null, "PNG không hợp lệ (độ dài chunk vượt file).");
            }

            string type = Encoding.ASCII.GetString(d.Slice(i + 4, 4));
            if (i == 8)
            {
                if (type != "IHDR" || len != 13)
                {
                    return (null, "PNG không hợp lệ (thiếu IHDR).");
                }

                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + 8, 4)), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + 12, 4)), int.MaxValue);
            }

            int total = 12 + (int)len;
            if (PngKeep.Contains(type))
            {
                output.Write(d.Slice(i, total)); // chép cả CRC — chunk giữ nguyên nên CRC vẫn đúng
            }

            i += total;
            if (type == "IEND")
            {
                end = true;
                break;
            }
        }

        if (!end || width == 0 || height == 0)
        {
            return (null, "PNG không hợp lệ (thiếu IEND hoặc kích thước).");
        }

        return (new SanitizedImage("image/png", "png", width, height, output.ToArray(), d.Length - (int)output.Length), null);
    }

    // ---------- WebP: RIFF + chunk FourCC/độ dài (little-endian, đệm chẵn) ----------

    private static readonly HashSet<string> WebPKeep = ["VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF", "ICCP"];

    private static (SanitizedImage?, string?) WebP(ReadOnlySpan<byte> d)
    {
        uint riff = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4, 4));
        if (riff < 4 || 8 + (long)riff > d.Length)
        {
            return (null, "WebP không hợp lệ (kích thước RIFF sai).");
        }

        int limit = 8 + (int)riff, i = 12, width = 0, height = 0;
        var chunks = new MemoryStream(d.Length);
        bool image = false;
        while (i + 8 <= limit)
        {
            string type = Encoding.ASCII.GetString(d.Slice(i, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i + 4, 4));
            long padded = size + (size & 1);
            if (i + 8 + padded > limit)
            {
                return (null, "WebP không hợp lệ (độ dài chunk vượt file).");
            }

            ReadOnlySpan<byte> payload = d.Slice(i + 8, (int)size);
            switch (type)
            {
                case "VP8X" when size >= 10:
                    width = 1 + (payload[4] | payload[5] << 8 | payload[6] << 16);
                    height = 1 + (payload[7] | payload[8] << 8 | payload[9] << 16);
                    break;
                case "VP8 " when size >= 10 && payload[3] == 0x9D && payload[4] == 0x01 && payload[5] == 0x2A:
                    image = true;
                    if (width == 0)
                    {
                        width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3FFF;
                        height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3FFF;
                    }

                    break;
                case "VP8L" when size >= 5 && payload[0] == 0x2F:
                    image = true;
                    if (width == 0)
                    {
                        width = 1 + ((payload[2] & 0x3F) << 8 | payload[1]);
                        height = 1 + ((payload[4] & 0x0F) << 10 | payload[3] << 2 | (payload[2] & 0xC0) >> 6);
                    }

                    break;
                case "ANMF":
                    image = true;
                    break;
            }

            if (WebPKeep.Contains(type))
            {
                byte[] chunk = d.Slice(i, 8 + (int)padded).ToArray();
                if (type == "VP8X")
                {
                    chunk[8] &= unchecked((byte)~0x0C); // bỏ cờ "có EXIF" (0x08) và "có XMP" (0x04) vì đã xoá hai chunk đó
                }

                chunks.Write(chunk);
            }

            i += 8 + (int)padded;
        }

        if (!image || width == 0 || height == 0)
        {
            return (null, "WebP không hợp lệ (thiếu dữ liệu ảnh).");
        }

        var output = new MemoryStream((int)chunks.Length + 12);
        output.Write("RIFF"u8);
        Span<byte> size4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size4, (uint)(4 + chunks.Length));
        output.Write(size4);
        output.Write("WEBP"u8);
        chunks.Position = 0;
        chunks.CopyTo(output);
        return (new SanitizedImage("image/webp", "webp", width, height, output.ToArray(), d.Length - (int)output.Length), null);
    }
}
