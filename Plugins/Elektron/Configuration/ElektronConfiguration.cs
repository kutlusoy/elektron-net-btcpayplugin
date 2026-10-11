using System;

namespace BTCPayServer.Plugins.Elektron.Configuration;

public class ElektronConfiguration
{
    public Uri DaemonUri { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string WalletName { get; set; } = "btcpay";
    public bool AllowHotWallet { get; set; }
    public bool IsConfigured => DaemonUri is not null;
}
