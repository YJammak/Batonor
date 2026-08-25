using Batonor.Abstractions;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Batonor.Activities;

/// <summary>
/// Built-in <c>http</c> activity: sends an HTTP request described by the node config
/// (<c>method</c>/<c>url</c>/<c>headers</c>/<c>body</c>/<c>timeoutMs</c> in the resolved input)
/// and returns the parsed response body.
/// </summary>
public sealed class HttpActivity : IActivity
{
    private readonly HttpClient _client;

    public HttpActivity() : this(new HttpClient()) { }

    public HttpActivity(HttpClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
    {
        var input = context.Input as JsonObject ?? new JsonObject();
        var method = input["method"]?.GetValue<string>() ?? "GET";
        var url = input["url"]?.GetValue<string>()
            ?? throw new BatonorException("HTTP activity requires a 'url'.");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (input["headers"] is JsonObject headers)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value?.GetValue<string>());
            }
        }

        if (input["body"] is JsonNode body)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (input["timeoutMs"]?.GetValue<int>() is { } timeoutMs && timeoutMs > 0)
        {
            cts.CancelAfter(timeoutMs);
        }

        using var response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        try
        {
            return JsonNode.Parse(responseText) ?? JsonValue.Create(responseText);
        }
        catch (JsonException)
        {
            // Non-JSON body (e.g. an HTML error page or plain text) — surface the raw text.
            return JsonValue.Create(responseText);
        }
    }
}
