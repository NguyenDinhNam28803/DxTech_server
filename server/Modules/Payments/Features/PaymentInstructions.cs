using Microsoft.Extensions.Options;
using server.Modules.Payments.Contracts;

namespace server.Modules.Payments.Features;

internal sealed class PaymentInstructions(IOptions<BankTransferOptions> options) : IPaymentInstructions
{
    private readonly BankTransferOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    public BankTransferInstruction? For(string orderCode, long amountVnd)
        => !_o.IsConfigured
            ? null
            : new BankTransferInstruction(_o.BankName, _o.BankBin, _o.AccountNumber, _o.AccountName, amountVnd, orderCode,
                VietQr.Build(_o.BankBin, _o.AccountNumber, amountVnd, orderCode));
}
