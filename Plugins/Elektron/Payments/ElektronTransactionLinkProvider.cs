using System.Globalization;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Elektron.Payments;

public class ElektronTransactionLinkProvider(string blockExplorerLink) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    // Payment IDs are "<txid>:<vout>"
    public override string GetTransactionLink(string paymentId)
    {
        if (string.IsNullOrEmpty(BlockExplorerLink))
        {
            return null;
        }
        return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, paymentId.Split(':')[0]);
    }
}
