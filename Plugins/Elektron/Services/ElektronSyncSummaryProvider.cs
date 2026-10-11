using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Elektron.Services;

public class ElektronSyncSummaryProvider(IElektronRpcProvider rpc) : ISyncSummaryProvider
{
    public string Partial => "/Views/Elektron/ElektronSyncSummary.cshtml";

    public bool AllAvailable()
        => !rpc.IsConfigured || rpc.Summary is { DaemonAvailable: true, Synced: true };

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        if (!rpc.IsConfigured)
        {
            return [];
        }

        return
        [
            new ElektronSyncStatus
            {
                Summary = rpc.Summary,
                PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(ElektronPlugin.CryptoCode).ToString()
            }
        ];
    }
}

public class ElektronSyncStatus : SyncStatus, ISyncStatus
{
    public override bool Available => Summary?.WalletAvailable ?? false;

    public ElektronSummary Summary { get; set; }
}
