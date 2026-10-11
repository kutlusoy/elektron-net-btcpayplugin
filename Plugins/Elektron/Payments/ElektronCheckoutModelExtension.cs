using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Elektron.Services;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Elektron.Payments;

public class ElektronCheckoutModelExtension : ICheckoutModelExtension
{
    private readonly BTCPayNetworkBase _network;
    private readonly IPaymentLinkExtension _paymentLinkExtension;

    public ElektronCheckoutModelExtension(
        PaymentMethodId paymentMethodId,
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
        BTCPayNetworkBase network)
    {
        PaymentMethodId = paymentMethodId;
        _network = network;
        _paymentLinkExtension = paymentLinkExtensions.Single(p => p.PaymentMethodId == paymentMethodId);
    }

    public PaymentMethodId PaymentMethodId { get; }
    public string Image => _network.CryptoImagePath;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ElektronPaymentMethodHandler handler })
        {
            return;
        }

        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;

        var details = context.InvoiceEntity.GetPayments(true)
            .Select(p => p.GetDetails<ElektronPaymentData>(handler))
            .FirstOrDefault(d => d is not null);
        if (details is not null)
        {
            context.Model.ReceivedConfirmations = details.ConfirmationCount;
            context.Model.RequiredConfirmations = ElektronListener.ConfirmationsRequired(details, context.InvoiceEntity.SpeedPolicy);
        }

        context.Model.InvoiceBitcoinUrl = _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
    }
}
