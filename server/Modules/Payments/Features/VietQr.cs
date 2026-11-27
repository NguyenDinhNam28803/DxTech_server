using System.Globalization;
using System.Text;

namespace server.Modules.Payments.Features;

/// <summary>
/// Dựng chuỗi VietQR theo chuẩn EMVCo QR / NAPAS (chuyển khoản tới tài khoản, có số tiền và nội dung) — chỉ ghép chuỗi,
/// không gọi dịch vụ ngoài (không gửi thông tin đơn cho bên thứ ba như khi dùng ảnh QR dựng sẵn).
/// Cấu trúc: mỗi trường = ID (2 số) + độ dài (2 số) + giá trị; trường cuối 63 là CRC16-CCITT (0x1021, khởi tạo 0xFFFF)
/// tính trên toàn chuỗi kể cả "6304".
/// ⚠️ Cần quét thử bằng app của vài ngân hàng trước khi mở bán (checklist nghiệm thu Phase 3).
/// </summary>
internal static class VietQr
{
    private const string NapasGuid = "A000000727";
    private const string ServiceToAccount = "QRIBFTTA";

    public static string Build(string bankBin, string accountNumber, long amountVnd, string content)
    {
        if (amountVnd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountVnd));
        }

        string beneficiary = Tlv("00", bankBin) + Tlv("01", accountNumber);
        string merchantAccount = Tlv("00", NapasGuid) + Tlv("01", beneficiary) + Tlv("02", ServiceToAccount);

        var sb = new StringBuilder()
            .Append(Tlv("00", "01"))                    // Payload format indicator
            .Append(Tlv("01", "12"))                    // Mã động (có số tiền, dùng một lần)
            .Append(Tlv("38", merchantAccount))         // NAPAS 247 — chuyển tới tài khoản
            .Append(Tlv("53", "704"))                   // VND
            .Append(Tlv("54", amountVnd.ToString(CultureInfo.InvariantCulture)))
            .Append(Tlv("58", "VN"))
            .Append(Tlv("62", Tlv("08", content)))      // Nội dung chuyển khoản = mã đơn
            .Append("6304");
        return sb.Append(Crc16(sb.ToString()).ToString("X4")).ToString();
    }

    internal static string Tlv(string id, string value)
    {
        if (value.Length > 99)
        {
            throw new ArgumentException($"Trường {id} quá dài.", nameof(value));
        }

        return id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;
    }

    /// <summary>CRC-16/CCITT-FALSE. Giá trị kiểm tra chuẩn: "123456789" ⇒ 0x29B1.</summary>
    internal static ushort Crc16(string data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in Encoding.UTF8.GetBytes(data))
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}
