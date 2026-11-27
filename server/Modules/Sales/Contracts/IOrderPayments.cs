namespace server.Modules.Sales.Contracts;

/// <summary>
/// Áp một khoản tiền đã xác thực vào đơn (ADR-013 §4) — module Payments gọi trong transaction của nó
/// (cùng <c>AppDbContext</c>, ADR-002) ⇒ ghi payment + đổi trạng thái đơn + giao key là một khối.
/// Sales giữ toàn bộ luật nghiệp vụ của đơn; Payments chỉ ghi nhận tiền.
/// </summary>
public interface IOrderPayments
{
    /// <param name="transferContent">Nội dung chuyển khoản gốc — Sales tự tìm mã đơn trong đó.</param>
    Task<PaymentApplication> ApplyAsync(string transferContent, long amountVnd, DateTimeOffset now, CancellationToken ct);
}

/// <param name="MatchStatus">matched | underpaid | overpaid | no_order_code | unknown_order | order_not_payable | late_no_stock
/// (giá trị của <c>payments.match_status</c>).</param>
/// <param name="OrderId">null khi không tìm được đơn.</param>
public sealed record PaymentApplication(string MatchStatus, Guid? OrderId, string? OrderCode);
