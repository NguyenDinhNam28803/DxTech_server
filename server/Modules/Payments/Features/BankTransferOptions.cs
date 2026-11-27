using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace server.Modules.Payments.Features;

/// <summary>
/// Tài khoản nhận tiền của công ty (ADR-013 §6) — thông tin công khai (in trên trang thanh toán), không phải bí mật.
/// Cấu hình <c>Payments:BankTransfer:*</c>; production qua biến môi trường. Để trống ⇒ API không nhận đặt hàng.
/// </summary>
internal sealed partial class BankTransferOptions : IValidatableObject
{
    public const string SectionName = "Payments:BankTransfer";

    /// <summary>Mã BIN 6 số của ngân hàng theo NAPAS (ví dụ 970436 = Vietcombank).</summary>
    public string BankBin { get; set; } = "";

    public string BankName { get; set; } = "";

    public string AccountNumber { get; set; } = "";

    /// <summary>Tên chủ tài khoản đúng như ngân hàng hiển thị (chữ hoa không dấu).</summary>
    public string AccountName { get; set; } = "";

    public bool IsConfigured => BankBin.Length > 0 && AccountNumber.Length > 0;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsConfigured && BankBin.Length == 0 && AccountNumber.Length == 0)
        {
            yield break; // chưa cấu hình: hợp lệ, chỉ là chưa mở đặt hàng
        }

        if (!BinRegex().IsMatch(BankBin))
        {
            yield return new ValidationResult("Payments:BankTransfer:BankBin phải là 6 chữ số.", [nameof(BankBin)]);
        }

        if (!AccountRegex().IsMatch(AccountNumber))
        {
            yield return new ValidationResult("Payments:BankTransfer:AccountNumber: 6–19 chữ số.", [nameof(AccountNumber)]);
        }

        if (string.IsNullOrWhiteSpace(AccountName) || string.IsNullOrWhiteSpace(BankName))
        {
            yield return new ValidationResult("Thiếu Payments:BankTransfer:AccountName / BankName.", [nameof(AccountName)]);
        }
    }

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex BinRegex();

    [GeneratedRegex("^[0-9]{6,19}$")]
    private static partial Regex AccountRegex();
}
