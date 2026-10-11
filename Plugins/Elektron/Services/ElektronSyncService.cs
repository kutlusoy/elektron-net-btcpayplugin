using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Elektron.Services;

public class ElektronSyncService(
    IElektronRpcProvider rpc,
    EventAggregator eventAggregator,
    ILogger<ElektronSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!rpc.IsConfigured)
        {
            logger.LogWarning("{CryptoCode} is disabled: BTCPAY_ELEK_DAEMON_URI is not configured", ElektronPlugin.CryptoCode);
            return;
        }

        var lastHeight = -1L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var summary = await rpc.UpdateSummary(stoppingToken);
                if (rpc.IsAvailable && summary.Height != lastHeight)
                {
                    lastHeight = summary.Height;
                    eventAggregator.Publish(new ElektronEvent
                    {
                        CryptoCode = ElektronPlugin.CryptoCode,
                        BlockHash = summary.Height.ToString()
                    });
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Unhandled exception while polling the {CryptoCode} node", ElektronPlugin.CryptoCode);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(rpc.IsAvailable ? 15 : 10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
