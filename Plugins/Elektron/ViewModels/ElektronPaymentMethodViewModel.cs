using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Elektron.Services;

namespace BTCPayServer.Plugins.Elektron.ViewModels;

public class ElektronPaymentMethodViewModel : IValidatableObject
{
    public bool Configured { get; set; }
    public bool AllowHotWallet { get; set; }
    public ElektronSummary Summary { get; set; }
    public bool Enabled { get; set; }

    [Display(Name = "Consider the invoice settled when the payment transaction ...")]
    public ElektronSettlementThresholdChoice SettlementConfirmationThresholdChoice { get; set; }

    [Display(Name = "Required Confirmations"), Range(0, 100)]
    public int? CustomSettlementConfirmationThreshold { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SettlementConfirmationThresholdChoice is ElektronSettlementThresholdChoice.Custom
            && CustomSettlementConfirmationThreshold is null)
        {
            yield return new ValidationResult(
                "You must specify the number of required confirmations when using a custom threshold.",
                [nameof(CustomSettlementConfirmationThreshold)]);
        }
    }
}

public enum ElektronSettlementThresholdChoice
{
    [Display(Name = "Store Speed Policy", Description = "Use the store's speed policy")]
    StoreSpeedPolicy,
    [Display(Name = "Zero Confirmation", Description = "Is unconfirmed")]
    ZeroConfirmation,
    [Display(Name = "At Least One", Description = "Has at least 1 confirmation")]
    AtLeastOne,
    [Display(Name = "At Least Six", Description = "Has at least 6 confirmations")]
    AtLeastSix,
    [Display(Name = "Custom", Description = "Custom")]
    Custom
}
