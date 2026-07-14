using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cairn.Mcp.Internal;

/// <summary>
/// The default invoker: executes an affordance by sending an HTTP request to its resolved target URL — the
/// exact request a hypermedia client would make — forwarding the MCP caller's <c>Authorization</c> header so
/// the endpoint authenticates the same user. Inputs are submitted as a JSON body (or as query parameters for
/// GET/HEAD), and the endpoint's own validation and authorization remain authoritative.
/// </summary>
internal sealed class HttpCairnMcpAffordanceInvoker : ICairnMcpAffordanceInvoker
{
    /// <summary>The named <see cref="HttpClient"/> the invoker sends through; configure it to customize the transport.</summary>
    public const string HttpClientName = "Cairn.Mcp";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CairnMcpOptions> _options;

    public HttpCairnMcpAffordanceInvoker(IHttpClientFactory httpClientFactory, IOptions<CairnMcpOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<CairnMcpAffordanceResult> InvokeAsync(CairnMcpAffordanceCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var affordance = call.Affordance;
        if (affordance.ContentType is { } contentType && !string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The default affordance invoker submits inputs as application/json; the '{affordance.Name.Value}' affordance declares '{contentType}'. Register a custom {nameof(ICairnMcpAffordanceInvoker)} to support it.");
        }

        var method = new HttpMethod(affordance.Method);
        using var request = new HttpRequestMessage(method, TargetOf(affordance.Href, call.HttpContext.Request, call.Arguments, method));
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(JsonBody(call.Arguments), Encoding.UTF8, "application/json");
        }

        var options = _options.Value;
        if (options.ForwardAuthorizationHeader && call.HttpContext.Request.Headers.Authorization is { Count: > 0 } authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", (IEnumerable<string?>)authorization);
        }

        options.ConfigureInvocationRequest?.Invoke(call.HttpContext, request);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new CairnMcpAffordanceResult(
            (int)response.StatusCode,
            string.IsNullOrEmpty(body) ? null : body,
            response.Content.Headers.ContentType?.ToString());
    }

    // The engine resolved the href for the MCP request itself, so a rooted path (LinkUrlStyle.PathRelative)
    // resolves against the host this MCP request arrived on — checked by shape rather than Uri.TryCreate,
    // which on Unix parses "/orders/1" as an absolute file:// URI. GET inputs travel as query parameters
    // (a body would be dropped, matching HAL-FORMS semantics); everything else goes as JSON.
    private static Uri TargetOf(string href, HttpRequest mcpRequest, IReadOnlyDictionary<string, JsonElement> arguments, HttpMethod method)
    {
        var url = href.StartsWith('/')
            ? new Uri(new Uri($"{mcpRequest.Scheme}://{mcpRequest.Host}", UriKind.Absolute), href)
            : new Uri(href, UriKind.Absolute);

        if (method == HttpMethod.Get && arguments.Count > 0)
        {
            var query = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in arguments)
            {
                query[name] = value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => value.GetString(),
                    _ => value.GetRawText(),
                };
            }

            url = new Uri(QueryHelpers.AddQueryString(url.AbsoluteUri, query), UriKind.Absolute);
        }

        return url;
    }

    private static string JsonBody(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
