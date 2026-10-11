using System.Linq;
using BTCPayServer.Plugins.Elektron.Services;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Elektron.Controllers;

[Route("[controller]")]
public class ElektronDaemonCallbackController(EventAggregator eventAggregator) : Controller
{
    [HttpGet("block")]
    public IActionResult OnBlockNotify(string hash)
    {
        if (!IsHash(hash))
        {
            return BadRequest();
        }
        eventAggregator.Publish(new ElektronEvent { CryptoCode = ElektronPlugin.CryptoCode, BlockHash = hash });
        return Ok();
    }

    [HttpGet("tx")]
    public IActionResult OnTransactionNotify(string hash)
    {
        if (!IsHash(hash))
        {
            return BadRequest();
        }
        eventAggregator.Publish(new ElektronEvent { CryptoCode = ElektronPlugin.CryptoCode, TransactionHash = hash });
        return Ok();
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
