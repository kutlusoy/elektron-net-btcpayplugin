using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Elektron.Payments;
using BTCPayServer.Plugins.Elektron.Services;
using BTCPayServer.Plugins.Elektron.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Elektron.Controllers;

// The wallet is shared by the whole instance, so server settings permission is required as well
[Route("stores/{storeId}/elektron")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIElektronStoreController(
    StoreRepository storeRepository,
    IElektronRpcProvider rpc,
    PaymentMethodHandlerDictionary handlers) : Controller
{
    private const string ViewPath = "/Views/Elektron/GetStoreElektronPaymentMethod.cshtml";
    private static readonly PaymentMethodId Pmi = PaymentTypes.CHAIN.GetPaymentMethodId(ElektronPlugin.CryptoCode);

    public StoreData StoreData => HttpContext.GetStoreData();

    [NonAction]
    public ElektronPaymentMethodViewModel GetVM(StoreData storeData)
    {
        storeData.GetPaymentMethodConfigs<ElektronPaymentMethodConfig>(handlers).TryGetValue(Pmi, out var config);

        var choice = ElektronSettlementThresholdChoice.StoreSpeedPolicy;
        if (config?.InvoiceSettledConfirmationThreshold is { } confirmations)
        {
            choice = confirmations switch
            {
                0 => ElektronSettlementThresholdChoice.ZeroConfirmation,
                1 => ElektronSettlementThresholdChoice.AtLeastOne,
                6 => ElektronSettlementThresholdChoice.AtLeastSix,
                _ => ElektronSettlementThresholdChoice.Custom
            };
        }

        return new ElektronPaymentMethodViewModel
        {
            Configured = rpc.IsConfigured,
            AllowHotWallet = rpc.AllowHotWallet,
            Summary = rpc.Summary,
            Enabled = config is not null && !storeData.GetStoreBlob().IsExcluded(Pmi),
            SettlementConfirmationThresholdChoice = choice,
            CustomSettlementConfirmationThreshold = choice is ElektronSettlementThresholdChoice.Custom
                ? config?.InvoiceSettledConfirmationThreshold
                : null
        };
    }

    [HttpGet("")]
    public IActionResult GetStoreElektronPaymentMethod()
    {
        return View(ViewPath, GetVM(StoreData));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GetStoreElektronPaymentMethod(ElektronPaymentMethodViewModel viewModel)
    {
        var storeData = StoreData;
        if (!ModelState.IsValid)
        {
            var vm = GetVM(storeData);
            vm.Enabled = viewModel.Enabled;
            vm.SettlementConfirmationThresholdChoice = viewModel.SettlementConfirmationThresholdChoice;
            vm.CustomSettlementConfirmationThreshold = viewModel.CustomSettlementConfirmationThreshold;
            return View(ViewPath, vm);
        }

        var blob = storeData.GetStoreBlob();
        storeData.SetPaymentMethodConfig(handlers[Pmi], new ElektronPaymentMethodConfig
        {
            InvoiceSettledConfirmationThreshold = viewModel.SettlementConfirmationThresholdChoice switch
            {
                ElektronSettlementThresholdChoice.ZeroConfirmation => 0,
                ElektronSettlementThresholdChoice.AtLeastOne => 1,
                ElektronSettlementThresholdChoice.AtLeastSix => 6,
                ElektronSettlementThresholdChoice.Custom when viewModel.CustomSettlementConfirmationThreshold is { } custom => custom,
                _ => null
            }
        });
        blob.SetExcluded(Pmi, !viewModel.Enabled);
        storeData.SetStoreBlob(blob);
        await storeRepository.UpdateStore(storeData);
        return RedirectToAction(nameof(GetStoreElektronPaymentMethod));
    }
}
