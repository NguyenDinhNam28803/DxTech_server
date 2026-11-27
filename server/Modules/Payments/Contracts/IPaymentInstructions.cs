namespace server.Modules.Payments.Contracts;

/// <summary>Hướng dẫn chuyển khoản cho một đơn (ADR-013 §2) — module Sales gọi khi trả đơn cho khách.</summary>
public interface IPaymentInstructions
{
    /// <summary>false khi chưa cấu hình tài khoản nhận tiền ⇒ chưa mở đặt hàng.</summary>
    bool IsConfigured { get; }

    /// <summary>null khi chưa cấu hình.</summary>
    BankTransferInstruction? For(string orderCode, long amountVnd);
}

/// <param name="VietQr">Chuỗi VietQR (EMVCo) — web vẽ thành mã QR; app ngân hàng tự điền số tiền + nội dung.</param>
public sealed record BankTransferInstruction(
    string BankName,
    string BankBin,
    string AccountNumber,
    string AccountName,
    long AmountVnd,
    string TransferContent,
    string VietQr);
