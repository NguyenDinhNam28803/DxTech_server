using server.Modules.Payments.Features;

namespace server.Modules.Payments;

/// <summary>
/// Module Payments: hộp thư webhook, giao dịch tiền vào, hướng dẫn chuyển khoản VietQR — schema <c>payments</c> (ADR-013).
/// PR-9: bảng. PR-11: hướng dẫn chuyển khoản. PR-12: webhook SePay.
/// </summary>
public static class PaymentsModule
{
    public const string Schema = "payments";

    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BankTransferOptions>()
            .Bind(configuration.GetSection(BankTransferOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<Contracts.IPaymentInstructions, PaymentInstructions>();
        return services;
    }
}
