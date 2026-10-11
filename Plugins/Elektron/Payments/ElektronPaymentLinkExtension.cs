using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Elektron.Payments;

public class ElektronPaymentLinkExtension(PaymentMethodId paymentMethodId, ElektronBtcPayNetwork network) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper urlHelper)
    {
        var due = prompt.Calculate().Due;
        return $"{network.UriScheme}:{prompt.Destination}?amount={due.ToString(CultureInfo.InvariantCulture)}";
    }
}
