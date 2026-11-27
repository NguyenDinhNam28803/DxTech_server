using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Modules.Payments.Features;
using server.Modules.Payments.Features.Sepay;
using server.Modules.Payments.Jobs;
using server.Modules.Sales.Contracts;

namespace server.Modules.Payments;

/// <summary>
/// Module Payments: hộp thư webhook, giao dịch tiền vào, hướng dẫn chuyển khoản VietQR — schema <c>payments</c> (ADR-013).
/// PR-9: bảng. PR-11: hướng dẫn chuyển khoản. PR-12: webhook SePay, xử lý lại, hàng chờ đối soát.
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

        services.AddOptions<SepayOptions>().Bind(configuration.GetSection(SepayOptions.SectionName));
        services.AddScoped(sp => new WebhookProcessor(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrderPayments>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SepayWebhookHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<WebhookProcessor>(), sp.GetRequiredService<IOptions<SepayOptions>>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SepayWebhookHandler>>()));

        services.AddSingleton<WebhookMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookMaintenance>());
        return services;
    }
}
