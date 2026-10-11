using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Elektron;
using BTCPayServer.Plugins.Elektron.Payments;
using BTCPayServer.Plugins.Elektron.Services;

using Moq;

using Newtonsoft.Json.Linq;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Elektron.Payments;

public class ElektronPaymentTests
{
    [Trait("Category", "Unit")]
    [Theory]
    [InlineData(SpeedPolicy.HighSpeed, 0)]
    [InlineData(SpeedPolicy.MediumSpeed, 1)]
    [InlineData(SpeedPolicy.LowMediumSpeed, 2)]
    [InlineData(SpeedPolicy.LowSpeed, 6)]
    public void SpeedPolicy_DeterminesRequiredConfirmations(SpeedPolicy policy, int expected)
    {
        Assert.Equal(expected, ElektronListener.ConfirmationsRequired(new ElektronPaymentData(), policy));
    }

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData(SpeedPolicy.HighSpeed, 3, 3)]
    [InlineData(SpeedPolicy.LowSpeed, 0, 0)]
    public void ConfiguredThreshold_OverridesSpeedPolicy(SpeedPolicy policy, int threshold, int expected)
    {
        var details = new ElektronPaymentData { InvoiceSettledConfirmationThreshold = threshold };

        Assert.Equal(expected, ElektronListener.ConfirmationsRequired(details, policy));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void PaymentMethodId_IsElekChain()
    {
        Assert.Equal("ELEK-CHAIN", CreateHandler().PaymentMethodId.ToString());
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void EmptyConfig_UsesStoreSpeedPolicy()
    {
        var config = CreateHandler().ParsePaymentMethodConfig(new JObject());

        Assert.Null(((ElektronPaymentMethodConfig)config).InvoiceSettledConfirmationThreshold);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void Config_RoundTripsThreshold()
    {
        var handler = CreateHandler();
        var json = JObject.FromObject(new ElektronPaymentMethodConfig { InvoiceSettledConfirmationThreshold = 2 }, handler.Serializer);

        var parsed = (ElektronPaymentMethodConfig)handler.ParsePaymentMethodConfig(json);

        Assert.Equal(2, parsed.InvoiceSettledConfirmationThreshold);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void PaymentDetails_RoundTrip()
    {
        var handler = CreateHandler();
        var details = new ElektronPaymentData
        {
            TransactionId = new string('a', 64),
            Vout = 1,
            BlockHeight = 120,
            ConfirmationCount = 4,
            InvoiceSettledConfirmationThreshold = 6
        };
        var json = JObject.FromObject(details, handler.Serializer);

        var parsed = (ElektronPaymentData)handler.ParsePaymentDetails(json);

        Assert.Equal(details.TransactionId, parsed.TransactionId);
        Assert.Equal(1, parsed.Vout);
        Assert.Equal(120, parsed.BlockHeight);
        Assert.Equal(4, parsed.ConfirmationCount);
        Assert.Equal(6, parsed.InvoiceSettledConfirmationThreshold);
    }

    private static ElektronPaymentMethodHandler CreateHandler()
    {
        var rpc = new Mock<IElektronRpcProvider>();
        rpc.SetupGet(x => x.IsConfigured).Returns(true);
        rpc.SetupGet(x => x.IsAvailable).Returns(true);

        var network = new ElektronBtcPayNetwork { CryptoCode = ElektronPlugin.CryptoCode, Divisibility = 8 };
        return new ElektronPaymentMethodHandler(network, rpc.Object);
    }
}
