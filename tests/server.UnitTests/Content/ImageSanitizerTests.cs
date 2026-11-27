using System.Buffers.Binary;
using System.Text;
using server.Modules.Content.Domain;

namespace server.UnitTests.Content;

/// <summary>
/// CT-3b: kiểm loại ảnh bằng magic bytes + bỏ metadata (ADR-015 §5). Ảnh thử được dựng đúng cấu trúc từng định dạng, có cài
/// EXIF chứa "GPS" và chuỗi đánh dấu — sau khi làm sạch, các chuỗi đó không được còn, phần dữ liệu ảnh phải giữ nguyên từng byte.
/// </summary>
public class ImageSanitizerTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("GPS-10.7769N-106.7009E-iPhone");

    public static byte[] Jpeg(int width = 640, int height = 480, bool exif = true, bool icc = true)
    {
        var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);
        Segment(s, 0xE0, "JFIF\0\x01\x02\0\0\x01\0\x01\0\0"u8.ToArray());
        if (exif)
        {
            Segment(s, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. Secret]);
            Segment(s, 0xED, [.. "Photoshop 3.0\0"u8.ToArray(), .. Secret]); // IPTC
            Segment(s, 0xFE, Secret);                                           // COM
        }

        if (icc)
        {
            Segment(s, 0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, 0xAB, 0xCD]);
        }

        Segment(s, 0xDB, new byte[65]);                                         // bảng lượng tử
        Segment(s, 0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        Segment(s, 0xC4, new byte[20]);                                         // Huffman
        Segment(s, 0xDA, [1, 1, 0, 0, 0x3F, 0]);                                // SOS
        s.Write([0x12, 0x34, 0xFF, 0x00, 0x56]);                                // dữ liệu nén (FF00 là byte nhồi hợp lệ)
        s.Write([0xFF, 0xD9]);
        return s.ToArray();

        static void Segment(MemoryStream s, byte marker, byte[] payload)
        {
            s.Write([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            s.Write(payload);
        }
    }

    public static byte[] Png(int width = 300, int height = 200, bool text = true)
    {
        var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(s, "IHDR", ihdr);
        if (text)
        {
            Chunk(s, "tEXt", [.. "Comment\0"u8.ToArray(), .. Secret]);
            Chunk(s, "eXIf", Secret);
        }

        Chunk(s, "IDAT", [0x78, 0x9C, 1, 2, 3]);
        Chunk(s, "IEND", []);
        return s.ToArray();

        static void Chunk(MemoryStream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            s.Write(len);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write([0, 0, 0, 0]); // CRC: không kiểm (giữ nguyên chunk)
        }
    }

    public static byte[] WebP(int width = 800, int height = 600, bool exif = true)
    {
        var chunks = new MemoryStream();
        byte[] vp8x = new byte[10];
        vp8x[0] = exif ? (byte)0x08 : (byte)0;
        WriteU24(vp8x.AsSpan(4), width - 1);
        WriteU24(vp8x.AsSpan(7), height - 1);
        Chunk(chunks, "VP8X", vp8x);
        byte[] vp8 = [0x10, 0, 0, 0x9D, 0x01, 0x2A, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0xAA];
        Chunk(chunks, "VP8 ", vp8);   // độ dài lẻ ⇒ có byte đệm
        if (exif)
        {
            Chunk(chunks, "EXIF", Secret);
            Chunk(chunks, "XMP ", Secret);
        }

        var s = new MemoryStream();
        s.Write("RIFF"u8);
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + chunks.Length));
        s.Write(size);
        s.Write("WEBP"u8);
        s.Write(chunks.ToArray());
        return s.ToArray();

        static void WriteU24(Span<byte> b, int v)
        {
            b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16);
        }

        static void Chunk(MemoryStream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(len);
            s.Write(data);
            if ((data.Length & 1) == 1)
            {
                s.WriteByte(0);
            }
        }
    }

    [Fact]
    public void Jpeg_keeps_image_data_and_color_profile_but_drops_exif_iptc_and_comments()
    {
        byte[] input = Jpeg();
        (SanitizedImage? img, string? error) = ImageSanitizer.Sanitize(input);

        Assert.Null(error);
        Assert.Equal(("image/jpeg", "jpg", 640, 480), (img!.ContentType, img.Extension, img.Width, img.Height));
        Assert.False(Contains(img.Bytes, Secret));
        Assert.False(Contains(img.Bytes, "Exif\0\0"u8.ToArray()));
        Assert.True(Contains(img.Bytes, "ICC_PROFILE\0"u8.ToArray()));
        Assert.True(Contains(img.Bytes, "JFIF"u8.ToArray()));
        Assert.Equal(input[^9..], img.Bytes[^9..]); // SOS → EOI y nguyên
        Assert.True(img.RemovedBytes > Secret.Length * 3);
        Assert.Equal(img.Bytes, ImageSanitizer.Sanitize(img.Bytes).Image!.Bytes); // làm sạch lần 2 không đổi gì
    }

    [Fact]
    public void Png_drops_text_and_exif_chunks()
    {
        (SanitizedImage? img, _) = ImageSanitizer.Sanitize(Png());
        Assert.Equal(("image/png", 300, 200), (img!.ContentType, img.Width, img.Height));
        Assert.False(Contains(img.Bytes, Secret));
        Assert.True(Contains(img.Bytes, "IDAT"u8.ToArray()) && Contains(img.Bytes, "IEND"u8.ToArray()));
    }

    [Fact]
    public void WebP_drops_exif_and_xmp_clears_their_flags_and_fixes_the_riff_size()
    {
        (SanitizedImage? img, _) = ImageSanitizer.Sanitize(WebP());
        Assert.Equal(("image/webp", "webp", 800, 600), (img!.ContentType, img.Extension, img.Width, img.Height));
        Assert.False(Contains(img.Bytes, Secret));
        Assert.Equal(img.Bytes.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(img.Bytes.AsSpan(4)));
        int vp8x = IndexOf(img.Bytes, "VP8X"u8.ToArray());
        Assert.Equal(0, img.Bytes[vp8x + 8] & 0x0C);
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("gif")]
    [InlineData("empty")]
    [InlineData("truncated-jpeg")]
    [InlineData("truncated-png")]
    [InlineData("jpeg-without-frame")]
    [InlineData("webp-riff-too-big")]
    public void Rejects_anything_that_is_not_a_complete_jpeg_png_or_webp(string kind)
    {
        byte[] data = kind switch
        {
            "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray(),
            "html" => "<html><script>alert(1)</script></html>"u8.ToArray(),
            "gif" => "GIF89a\x01\0\x01\0"u8.ToArray(),
            "empty" => [],
            "truncated-jpeg" => Jpeg()[..40],
            "truncated-png" => Png()[..30],
            "jpeg-without-frame" => [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xD9],
            _ => WebP().Select((b, i) => i == 4 ? (byte)0xFF : b).ToArray(),
        };

        (SanitizedImage? img, string? error) = ImageSanitizer.Sanitize(data);

        Assert.Null(img);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Polyglot_html_appended_after_a_png_is_dropped()
    {
        byte[] input = [.. Png(text: false), .. "<script>alert(1)</script>"u8.ToArray()];
        (SanitizedImage? img, _) = ImageSanitizer.Sanitize(input);
        Assert.False(Contains(img!.Bytes, "<script>"u8.ToArray())); // mọi thứ sau IEND bị bỏ
    }

    private static bool Contains(byte[] haystack, byte[] needle) => IndexOf(haystack, needle) >= 0;

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
