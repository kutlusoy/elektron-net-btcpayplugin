using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Elektron.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Elektron.Services;

public class ElektronListener(
    InvoiceRepository invoiceRepository,
    EventAggregator eventAggregator,
    IElektronRpcProvider rpc,
    ILogger<ElektronListener> logger,
    PaymentMethodHandlerDictionary handlers,
    InvoiceActivator invoiceActivator,
    PaymentService paymentService) : EventHostedServiceBase(eventAggregator, logger)
{
    private static readonly PaymentMethodId Pmi = PaymentTypes.CHAIN.GetPaymentMethodId(ElektronPlugin.CryptoCode);

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();
        Subscribe<ElektronEvent>();
        Subscribe<ElektronDaemonStateChange>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (!rpc.IsAvailable)
        {
            return;
        }

        switch (evt)
        {
            case ElektronDaemonStateChange:
                await UpdatePendingPayments(cancellationToken);
                break;
            case ElektronEvent { BlockHash: { Length: > 0 } }:
                await UpdatePendingPayments(cancellationToken);
                eventAggregator.Publish(new NewBlockEvent { PaymentMethodId = Pmi });
                break;
            case ElektronEvent { TransactionHash: { Length: > 0 } txId }:
                await OnTransactionUpdated(txId, cancellationToken);
                break;
        }
    }

    private async Task UpdatePendingPayments(CancellationToken cancellationToken)
    {
        var invoices = await invoiceRepository.GetMonitoredInvoices(Pmi);
        var updated = new List<(PaymentEntity Payment, InvoiceEntity Invoice)>();

        foreach (var invoice in invoices)
        {
            var prompt = invoice.GetPaymentPrompt(Pmi);
            if (prompt is not { Activated: true } || string.IsNullOrEmpty(prompt.Destination))
            {
                continue;
            }

            foreach (var txId in await rpc.Client.GetTxIdsForAddress(prompt.Destination, cancellationToken))
            {
                foreach (var receive in await rpc.Client.GetReceives(txId, cancellationToken))
                {
                    if (receive.Address == prompt.Destination)
                    {
                        await HandleReceive(invoice, receive, updated);
                    }
                }
            }
        }
        await SaveUpdates(updated);
    }

    private async Task OnTransactionUpdated(string txId, CancellationToken cancellationToken)
    {
        var updated = new List<(PaymentEntity Payment, InvoiceEntity Invoice)>();
        foreach (var receive in await rpc.Client.GetReceives(txId, cancellationToken))
        {
            var invoice = await invoiceRepository.GetInvoiceFromAddress(Pmi, receive.Address);
            if (invoice is not null)
            {
                await HandleReceive(invoice, receive, updated);
            }
        }
        await SaveUpdates(updated);
    }

    private async Task HandleReceive(
        InvoiceEntity invoice,
        ElektronReceive receive,
        List<(PaymentEntity Payment, InvoiceEntity Invoice)> updated)
    {
        var handler = (ElektronPaymentMethodHandler)handlers[Pmi];
        var promptDetails = handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(Pmi).Details);
        var details = new ElektronPaymentData
        {
            TransactionId = receive.TxId,
            Vout = receive.Vout,
            BlockHeight = receive.BlockHeight,
            ConfirmationCount = receive.Confirmations,
            InvoiceSettledConfirmationThreshold = promptDetails.InvoiceSettledConfirmationThreshold
        };
        var status = ConfirmationsRequired(details, invoice.SpeedPolicy) <= details.ConfirmationCount
            ? PaymentStatus.Settled
            : PaymentStatus.Processing;

        // Same id format as Bitcoin outpoints, which BTCPay's transaction link providers expect
        var paymentId = $"{receive.TxId}-{receive.Vout}";

        var existing = invoice.GetPayments(false)
            .SingleOrDefault(p => p.PaymentMethodId == Pmi && p.Id == paymentId);
        if (existing is not null)
        {
            existing.Status = status;
            existing.Details = JToken.FromObject(details, handler.Serializer);
            updated.Add((existing, invoice));
            return;
        }

        var paymentData = new PaymentData
        {
            Status = status,
            Amount = receive.Amount,
            Created = DateTimeOffset.UtcNow,
            Id = paymentId,
            Currency = ElektronPlugin.CryptoCode,
            InvoiceDataId = invoice.Id
        }.Set(invoice, handler, details);

        var payment = await paymentService.AddPayment(paymentData, [receive.TxId]);
        if (payment is not null)
        {
            await ReceivedPayment(invoice, payment);
        }
    }

    private async Task SaveUpdates(List<(PaymentEntity Payment, InvoiceEntity Invoice)> updated)
    {
        if (updated.Count == 0)
        {
            return;
        }
        await paymentService.UpdatePayments(updated.Select(u => u.Payment).ToList());
        foreach (var invoiceId in updated.Select(u => u.Invoice.Id).Distinct())
        {
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
        }
    }

    private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation("Invoice {InvoiceId} received payment {Value} {Currency} {PaymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);
        if (prompt is { Activated: true } &&
            prompt.Destination == payment.Destination &&
            prompt.Calculate().Due > 0.0m)
        {
            await invoiceActivator.ActivateInvoicePaymentMethod(invoice.Id, payment.PaymentMethodId, true);
            invoice = await invoiceRepository.GetInvoice(invoice.Id);
        }

        eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    public static int ConfirmationsRequired(ElektronPaymentData details, SpeedPolicy speedPolicy)
        => details.InvoiceSettledConfirmationThreshold ?? speedPolicy switch
        {
            SpeedPolicy.HighSpeed => 0,
            SpeedPolicy.MediumSpeed => 1,
            SpeedPolicy.LowMediumSpeed => 2,
            SpeedPolicy.LowSpeed => 6,
            _ => 6
        };
}
