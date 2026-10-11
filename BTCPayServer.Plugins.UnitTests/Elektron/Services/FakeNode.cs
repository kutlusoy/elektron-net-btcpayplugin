using System.Net;
using System.Text;

using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.UnitTests.Elektron.Services;

internal sealed class FakeNode : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, Func<int, (HttpStatusCode Status, string Body)>> _methods = new();
    private readonly Dictionary<string, int> _counts = new();

    public List<string> Calls { get; } = [];

    public FakeNode Respond(string method, JToken result)
        => On(method, _ => (HttpStatusCode.OK, Envelope(result, null)));

    public FakeNode Fail(string method, int code, string message)
        => On(method, _ => (HttpStatusCode.InternalServerError, Envelope(null, new JObject { ["code"] = code, ["message"] = message })));

    public FakeNode On(string method, Func<int, (HttpStatusCode Status, string Body)> responder)
    {
        _methods[method] = responder;
        return this;
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var method = (string)body["method"]!;
        Calls.Add(method);

        if (!_methods.TryGetValue(method, out var responder))
        {
            throw new HttpRequestException($"node unreachable ({method})");
        }

        var index = _counts.GetValueOrDefault(method);
        _counts[method] = index + 1;
        var (status, content) = responder(index);
        return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }

    private static string Envelope(JToken? result, JToken? error)
        => new JObject { ["result"] = result, ["error"] = error, ["id"] = "btcpay" }.ToString();
}
