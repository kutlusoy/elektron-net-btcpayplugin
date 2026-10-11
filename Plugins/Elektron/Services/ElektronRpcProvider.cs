using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Elektron.Configuration;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Elektron.Services;

public class ElektronSummary
{
    public bool DaemonAvailable { get; set; }
    public bool WalletAvailable { get; set; }
    public bool WalletWatchOnly { get; set; }
    public bool Synced { get; set; }
    public long Height { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ElektronDaemonStateChange
{
    public ElektronSummary Summary { get; set; }
}

public class ElektronEvent
{
    public string CryptoCode { get; set; }
    public string BlockHash { get; set; }
    public string TransactionHash { get; set; }
}

public interface IElektronRpcProvider
{
    bool IsConfigured { get; }
    bool IsAvailable { get; }
    ElektronRpcClient Client { get; }
    ElektronSummary Summary { get; }
    Task<ElektronSummary> UpdateSummary(CancellationToken cancellationToken = default);
}

public class ElektronRpcProvider(
    ElektronConfiguration configuration,
    ElektronRpcClient client,
    EventAggregator eventAggregator) : IElektronRpcProvider
{
    private volatile ElektronSummary _summary = new();

    public bool IsConfigured => configuration.IsConfigured;
    public ElektronRpcClient Client => client;
    public ElektronSummary Summary => _summary;

    // A wallet holding private keys is rejected: the plugin must stay non-custodial
    public bool IsAvailable => _summary is { DaemonAvailable: true, WalletAvailable: true, WalletWatchOnly: true, Synced: true };

    public async Task<ElektronSummary> UpdateSummary(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _summary;
        }

        var summary = new ElektronSummary { UpdatedAt = DateTime.UtcNow };
        try
        {
            var info = await client.GetBlockchainInfo(cancellationToken);
            summary.Height = (long)info["blocks"];
            summary.Synced = !(bool)info["initialblockdownload"];
            summary.DaemonAvailable = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            summary.DaemonAvailable = false;
        }

        if (summary.DaemonAvailable)
        {
            try
            {
                var wallet = await GetWalletInfo(cancellationToken);
                summary.WalletWatchOnly = (bool?)wallet["private_keys_enabled"] == false;
                summary.WalletAvailable = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                summary.WalletAvailable = false;
            }
        }

        var wasAvailable = IsAvailable;
        _summary = summary;
        if (wasAvailable != IsAvailable)
        {
            eventAggregator.Publish(new ElektronDaemonStateChange { Summary = summary });
        }
        return summary;
    }

    // The node does not reload wallets after a restart unless asked to
    private async Task<JObject> GetWalletInfo(CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetWalletInfo(cancellationToken);
        }
        catch (ElektronRpcException ex) when (ex.Code == ElektronRpcClient.WalletNotFound)
        {
            await client.LoadWallet(cancellationToken);
            return await client.GetWalletInfo(cancellationToken);
        }
    }
}
