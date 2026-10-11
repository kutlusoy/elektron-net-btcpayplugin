using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Elektron.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Elektron.Payments;

public class ElektronPaymentMethodHandler(
    ElektronBtcPayNetwork network,
    IElektronRpcProvider rpc) : IPaymentMethodHandler
{
    private sealed record Prepare(Task<string> ReserveAddress);

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    public PaymentMethodId PaymentMethodId { get; } = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = network.CryptoCode;
        context.Prompt.Divisibility = network.Divisibility;
        if (context.Prompt.Activated && rpc.IsAvailable)
        {
            context.State = new Prepare(rpc.Client.GetNewAddress($"btcpay invoice {context.InvoiceEntity.Id}"));
        }
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!rpc.IsConfigured)
        {
            throw new PaymentMethodUnavailableException("BTCPAY_ELEK_DAEMON_URI is not configured");
        }
        if (!rpc.IsAvailable || context.State is not Prepare prepare)
        {
            throw new PaymentMethodUnavailableException("Node or wallet not available");
        }

        var address = await prepare.ReserveAddress;
        var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);

        context.Prompt.Destination = address;
        context.Prompt.Details = JObject.FromObject(new ElektronPromptDetails
        {
            InvoiceSettledConfirmationThreshold = config.InvoiceSettledConfirmationThreshold
        }, Serializer);
        context.TrackedDestinations.Add(address);
    }

    public ElektronPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        => config.ToObject<ElektronPaymentMethodConfig>(Serializer) ?? new ElektronPaymentMethodConfig();

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public ElektronPromptDetails ParsePaymentPromptDetails(JToken details)
        => details.ToObject<ElektronPromptDetails>(Serializer) ?? new ElektronPromptDetails();

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public ElektronPaymentData ParsePaymentDetails(JToken details)
        => details.ToObject<ElektronPaymentData>(Serializer)
           ?? throw new FormatException($"Invalid {nameof(ElektronPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
