namespace BTCPayServer.Plugins.Elektron.Payments;

public class ElektronPaymentMethodConfig
{
    public int? InvoiceSettledConfirmationThreshold { get; set; }
}

public class ElektronPromptDetails
{
    public int? InvoiceSettledConfirmationThreshold { get; set; }
}

public class ElektronPaymentData
{
    public string TransactionId { get; set; }
    public int Vout { get; set; }
    public long BlockHeight { get; set; }
    public int ConfirmationCount { get; set; }
    public int? InvoiceSettledConfirmationThreshold { get; set; }
}
