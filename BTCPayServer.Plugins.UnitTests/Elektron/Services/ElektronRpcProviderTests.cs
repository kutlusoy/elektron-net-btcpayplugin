using System.Net;

using BTCPayServer.Logging;
using BTCPayServer.Plugins.Elektron.Configuration;
using BTCPayServer.Plugins.Elektron.Services;

using Newtonsoft.Json.Linq;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Elektron.Services;

public class ElektronRpcProviderTests
{
    private static ElektronRpcProvider Create(FakeNode node, bool allowHotWallet = false)
    {
        var configuration = new ElektronConfiguration
        {
            DaemonUri = new Uri("http://127.0.0.1:8332"),
            AllowHotWallet = allowHotWallet
        };
        return new ElektronRpcProvider(
            configuration,
            new ElektronRpcClient(node, configuration),
            new EventAggregator(new Logs()));
    }

    private static FakeNode SyncedNode(bool privateKeys)
        => new FakeNode()
            .Respond("getblockchaininfo", new JObject { ["blocks"] = 250, ["initialblockdownload"] = false })
            .Respond("getwalletinfo", new JObject { ["private_keys_enabled"] = privateKeys });

    [Trait("Category", "Unit")]
    [Fact]
    public async Task WatchOnlyWallet_OnSyncedNode_IsAvailable()
    {
        var provider = Create(SyncedNode(privateKeys: false));

        var summary = await provider.UpdateSummary();

        Assert.True(provider.IsAvailable);
        Assert.True(summary.WalletWatchOnly);
        Assert.Equal(250, summary.Height);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task WalletWithPrivateKeys_IsRejectedByDefault()
    {
        var provider = Create(SyncedNode(privateKeys: true));

        var summary = await provider.UpdateSummary();

        Assert.False(provider.IsAvailable);
        Assert.False(summary.WalletWatchOnly);
        Assert.True(summary.WalletAvailable);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task WalletWithPrivateKeys_IsAcceptedInHotWalletMode()
    {
        var provider = Create(SyncedNode(privateKeys: true), allowHotWallet: true);

        await provider.UpdateSummary();

        Assert.True(provider.IsAvailable);
        Assert.True(provider.AllowHotWallet);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task NodeInInitialBlockDownload_IsNotAvailable()
    {
        var node = new FakeNode()
            .Respond("getblockchaininfo", new JObject { ["blocks"] = 10, ["initialblockdownload"] = true })
            .Respond("getwalletinfo", new JObject { ["private_keys_enabled"] = false });
        var provider = Create(node);

        var summary = await provider.UpdateSummary();

        Assert.False(provider.IsAvailable);
        Assert.False(summary.Synced);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task UnreachableNode_IsNotAvailable()
    {
        var provider = Create(new FakeNode());

        var summary = await provider.UpdateSummary();

        Assert.False(provider.IsAvailable);
        Assert.False(summary.DaemonAvailable);
        Assert.False(summary.WalletAvailable);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task UnloadedWallet_IsLoadedAndChecked_Again()
    {
        var node = new FakeNode()
            .Respond("getblockchaininfo", new JObject { ["blocks"] = 250, ["initialblockdownload"] = false })
            .Respond("loadwallet", new JObject { ["name"] = "btcpay" })
            .On("getwalletinfo", call => call == 0
                ? (HttpStatusCode.NotFound, new JObject
                {
                    ["result"] = null,
                    ["error"] = new JObject { ["code"] = -18, ["message"] = "Requested wallet does not exist or is not loaded" }
                }.ToString())
                : (HttpStatusCode.OK, new JObject
                {
                    ["result"] = new JObject { ["private_keys_enabled"] = false },
                    ["error"] = null
                }.ToString()));
        var provider = Create(node);

        await provider.UpdateSummary();

        Assert.True(provider.IsAvailable);
        Assert.Equal(new[] { "getblockchaininfo", "getwalletinfo", "loadwallet", "getwalletinfo" }, node.Calls);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task MissingWalletFile_StaysUnavailable()
    {
        var node = new FakeNode()
            .Respond("getblockchaininfo", new JObject { ["blocks"] = 250, ["initialblockdownload"] = false })
            .Fail("getwalletinfo", -18, "Requested wallet does not exist or is not loaded")
            .Fail("loadwallet", -18, "Wallet file not found");
        var provider = Create(node);

        var summary = await provider.UpdateSummary();

        Assert.False(provider.IsAvailable);
        Assert.True(summary.DaemonAvailable);
        Assert.False(summary.WalletAvailable);
    }
}
