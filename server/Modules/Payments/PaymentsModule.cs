namespace server.Modules.Payments;

/// <summary>
/// Module Payments: hộp thư webhook và giao dịch tiền vào — schema PostgreSQL <c>payments</c> (ADR-013).
/// PR-9 chỉ có Domain + Persistence; webhook SePay ở PR-12.
/// </summary>
public static class PaymentsModule
{
    public const string Schema = "payments";
}
