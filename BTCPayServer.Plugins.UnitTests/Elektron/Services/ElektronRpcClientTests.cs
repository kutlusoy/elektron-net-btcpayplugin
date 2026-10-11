using System.Net;
using System.Text;

using BTCPayServer.Plugins.Elektron.Configuration;
using BTCPayServer.Plugins.Elektron.Services;

using Newtonsoft.Json.Linq;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Elektron.Services;

public class ElektronRpcClientTests
{
    private const string Txid = "11111111111111111111111111111111111111111111111111111111111111aa";
    private const string Address = "be1qexampleaddress";

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public JObject? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ElektronRpcClient Client, StubHandler Handler) Create(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(status, body);
        var configuration = new ElektronConfiguration
        {
            DaemonUri = new Uri("http://127.0.0.1:8332"),
            Username = "user",
            Password = "pass"
        };
        return (new ElektronRpcClient(new StubFactory(handler), configuration), handler);
    }

    private static string Result(JToken result)
        => new JObject { ["result"] = result, ["error"] = null, ["id"] = "btcpay" }.ToString();

    private static string Error(int code, string message)
        => new JObject { ["result"] = null, ["error"] = new JObject { ["code"] = code, ["message"] = message }, ["id"] = "btcpay" }.ToString();

    [Trait("Category", "Unit")]
    [Fact]
    public async Task WalletCalls_UseWalletEndpointAndBasicAuth()
    {
        var (client, handler) = Create(Result("be1qnew"));

        await client.GetNewAddress("label");

        Assert.Equal("http://127.0.0.1:8332/wallet/btcpay", handler.Request!.RequestUri!.ToString());
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.Equal(expected, handler.Request.Headers.Authorization!.Parameter);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task DaemonCalls_UseRootEndpoint()
    {
        var (client, handler) = Create(Result(new JObject { ["blocks"] = 10 }));

        await client.GetBlockchainInfo();

        Assert.Equal("http://127.0.0.1:8332/", handler.Request!.RequestUri!.ToString());
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetNewAddress_RequestsBech32()
    {
        var (client, handler) = Create(Result("be1qnew"));

        var address = await client.GetNewAddress("btcpay invoice abc");

        Assert.Equal("be1qnew", address);
        Assert.Equal("getnewaddress", (string?)handler.RequestBody!.GetValue("method"));
        Assert.Equal(new[] { "btcpay invoice abc", "bech32" }, handler.RequestBody["params"]!.Select(p => (string?)p));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RpcError_ThrowsExceptionWithCode()
    {
        var (client, _) = Create(Error(-18, "Requested wallet does not exist or is not loaded"), HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<ElektronRpcException>(() => client.GetWalletInfo());

        Assert.Equal(ElektronRpcClient.WalletNotFound, ex.Code);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetReceives_ReturnsOnlyReceiveEntries()
    {
        var tx = new JObject
        {
            ["confirmations"] = 3,
            ["blockheight"] = 100,
            ["details"] = new JArray
            {
                new JObject { ["category"] = "receive", ["vout"] = 1, ["address"] = Address, ["amount"] = 0.5m },
                new JObject { ["category"] = "send", ["vout"] = 0, ["address"] = "be1qother", ["amount"] = -0.5m }
            }
        };
        var (client, _) = Create(Result(tx));

        var receives = await client.GetReceives(Txid);

        var receive = Assert.Single(receives);
        Assert.Equal(new ElektronReceive(Txid, 1, Address, 0.5m, 3, 100), receive);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetReceives_IgnoresConflictedTransactions()
    {
        var tx = new JObject
        {
            ["confirmations"] = -1,
            ["details"] = new JArray { new JObject { ["category"] = "receive", ["vout"] = 0, ["address"] = Address, ["amount"] = 1m } }
        };
        var (client, _) = Create(Result(tx));

        Assert.Empty(await client.GetReceives(Txid));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetReceives_IgnoresAbandonedEntries()
    {
        var tx = new JObject
        {
            ["confirmations"] = 0,
            ["details"] = new JArray { new JObject { ["category"] = "receive", ["abandoned"] = true, ["vout"] = 0, ["address"] = Address, ["amount"] = 1m } }
        };
        var (client, _) = Create(Result(tx));

        Assert.Empty(await client.GetReceives(Txid));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetReceives_UnknownTransaction_ReturnsEmpty()
    {
        var (client, _) = Create(Error(-5, "Invalid or non-wallet transaction id"), HttpStatusCode.InternalServerError);

        Assert.Empty(await client.GetReceives(Txid));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task GetTxIdsForAddress_RemovesDuplicates()
    {
        var result = new JArray
        {
            new JObject { ["address"] = Address, ["txids"] = new JArray(Txid, Txid) },
            new JObject { ["address"] = Address, ["txids"] = new JArray(Txid) }
        };
        var (client, _) = Create(Result(result));

        var txIds = await client.GetTxIdsForAddress(Address);

        Assert.Equal(new[] { Txid }, txIds);
    }
}
