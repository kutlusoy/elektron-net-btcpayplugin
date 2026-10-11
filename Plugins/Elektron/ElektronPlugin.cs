using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Elektron.Configuration;
using BTCPayServer.Plugins.Elektron.Payments;
using BTCPayServer.Plugins.Elektron.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Elektron;

public class ElektronPlugin : BaseBTCPayServerPlugin
{
    public const string CryptoCode = "ELEK";

    public override Version Version => new(0, 1, 0);

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var chainName = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>().NetworkType;

        var network = new ElektronBtcPayNetwork
        {
            CryptoCode = CryptoCode,
            DisplayName = "Elektron Net",
            Divisibility = 8,
            UriScheme = "elek"
        };
        var explorerLink = chainName == ChainName.Mainnet ? "https://mempool.elektron-net.org/tx/{0}" : null;
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(CryptoCode);

        services.AddDefaultPrettyName(pmi, network.DisplayName);
        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(pmi, new ElektronTransactionLinkProvider(explorerLink));

        services.AddSingleton(ReadConfiguration);
        services.AddHttpClient(ElektronRpcClient.HttpClientName);
        services.AddSingleton<ElektronRpcClient>();
        services.AddSingleton<IElektronRpcProvider, ElektronRpcProvider>();
        services.AddHostedService<ElektronSyncService>();
        services.AddHostedService<ElektronListener>();

        services.AddSingleton(provider =>
            (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ElektronPaymentMethodHandler), network));
        services.AddSingleton(provider =>
            (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ElektronPaymentLinkExtension), network, pmi));
        services.AddSingleton(provider =>
            (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ElektronCheckoutModelExtension), network, pmi));
    }

    private static ElektronConfiguration ReadConfiguration(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return new ElektronConfiguration
        {
            DaemonUri = configuration.GetOrDefault<Uri>("elek_daemon_uri", null),
            Username = configuration.GetOrDefault<string>("elek_daemon_username", null),
            Password = configuration.GetOrDefault<string>("elek_daemon_password", null),
            WalletName = configuration.GetOrDefault<string>("elek_wallet_name", "btcpay")
        };
    }
}
