using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Elektron.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Elektron.Services;

public class ElektronRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public record ElektronReceive(string TxId, int Vout, string Address, decimal Amount, int Confirmations, long BlockHeight);

public class ElektronRpcClient(IHttpClientFactory httpClientFactory, ElektronConfiguration configuration)
{
    public const string HttpClientName = "ELEKclient";
    public const int WalletNotFound = -18;
    private const int InvalidAddressOrKey = -5;
    private const int WalletAlreadyLoaded = -35;

    public async Task<JToken> CallAsync(string method, object[] args, bool wallet, CancellationToken cancellationToken = default)
    {
        var baseUri = configuration.DaemonUri.ToString().TrimEnd('/');
        var uri = wallet ? $"{baseUri}/wallet/{Uri.EscapeDataString(configuration.WalletName)}" : baseUri;
        var body = new JObject
        {
            ["jsonrpc"] = "1.0",
            ["id"] = "btcpay",
            ["method"] = method,
            ["params"] = new JArray(args)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(configuration.Username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{configuration.Username}:{configuration.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        // elektrond answers RPC errors with a non-2xx status and a JSON body
        JObject json;
        try
        {
            json = JObject.Parse(text);
        }
        catch (JsonException)
        {
            throw new HttpRequestException($"Unexpected response ({(int)response.StatusCode}) to {method}");
        }

        if (json["error"] is { Type: not JTokenType.Null } error)
        {
            throw new ElektronRpcException((int)error["code"], (string)error["message"] ?? "RPC error");
        }
        return json["result"] ?? JValue.CreateNull();
    }

    public async Task<JObject> GetBlockchainInfo(CancellationToken cancellationToken = default)
        => (JObject)await CallAsync("getblockchaininfo", [], false, cancellationToken);

    public async Task<JObject> GetWalletInfo(CancellationToken cancellationToken = default)
        => (JObject)await CallAsync("getwalletinfo", [], true, cancellationToken);

    public async Task LoadWallet(CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync("loadwallet", [configuration.WalletName], false, cancellationToken);
        }
        catch (ElektronRpcException ex) when (ex.Code == WalletAlreadyLoaded)
        {
            // nothing to do
        }
    }

    // Legacy Base58 addresses are identical to Bitcoin mainnet, so Bech32 is requested explicitly
    public async Task<string> GetNewAddress(string label, CancellationToken cancellationToken = default)
        => (string)await CallAsync("getnewaddress", [label, "bech32"], true, cancellationToken);

    public async Task<IReadOnlyList<string>> GetTxIdsForAddress(string address, CancellationToken cancellationToken = default)
    {
        var result = (JArray)await CallAsync("listreceivedbyaddress", [0, false, true, address], true, cancellationToken);
        return result
            .SelectMany(entry => entry["txids"]?.Select(txId => (string)txId) ?? Enumerable.Empty<string>())
            .Distinct()
            .ToList();
    }

    public async Task<IReadOnlyList<ElektronReceive>> GetReceives(string txId, CancellationToken cancellationToken = default)
    {
        JObject tx;
        try
        {
            tx = (JObject)await CallAsync("gettransaction", [txId, true], true, cancellationToken);
        }
        catch (ElektronRpcException ex) when (ex.Code == InvalidAddressOrKey)
        {
            return [];
        }

        var confirmations = (int?)tx["confirmations"] ?? 0;
        if (confirmations < 0)
        {
            return [];
        }
        var blockHeight = (long?)tx["blockheight"] ?? 0;

        return tx["details"]
            .OfType<JObject>()
            .Where(d => (string)d["category"] == "receive" && (bool?)d["abandoned"] != true)
            .Select(d => new ElektronReceive(txId, (int)d["vout"], (string)d["address"], (decimal)d["amount"], confirmations, blockHeight))
            .ToList();
    }
}
