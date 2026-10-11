using BTCPayServer.Plugins.Elektron.Configuration;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Elektron.Configuration;

public class ElektronConfigurationTests
{
    [Trait("Category", "Unit")]
    [Fact]
    public void DefaultConfiguration_IsNotConfigured()
    {
        var configuration = new ElektronConfiguration();

        Assert.False(configuration.IsConfigured);
        Assert.Null(configuration.DaemonUri);
        Assert.Equal("btcpay", configuration.WalletName);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void DaemonUri_MakesConfigurationConfigured()
    {
        var configuration = new ElektronConfiguration { DaemonUri = new Uri("http://127.0.0.1:8332") };

        Assert.True(configuration.IsConfigured);
    }
}
