using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

// The client threat-models hostile servers: the SSRF/link policy must survive consumers replacing the
// primary handler, credentials must not leak across origins on redirects, and a huge body must not be
// buffered without bound.
public class CairnClientHardeningTests
{
    [Fact]
    public async Task A_replaced_primary_handler_still_has_auto_redirect_disabled()
    {
        // A consumer swapping the primary handler (proxy, mTLS, ...) must not silently re-enable
        // in-handler redirects the link policy can never inspect.
        var primary = new HttpClientHandler { AllowAutoRedirect = true };

        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = _ => true;
        }).ConfigurePrimaryHttpMessageHandler(() => primary);

        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<CairnClient>();   // building the typed client builds the handler pipeline

        Assert.False(primary.AllowAutoRedirect);
    }

    [Fact]
    public async Task A_primary_handler_that_follows_redirects_itself_fails_loudly()
    {
        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = _ => true;
        }).ConfigurePrimaryHttpMessageHandler(() => new AutoFollowingStub());

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CairnClient>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<JsonElement>("/start"));
        Assert.Contains("redirect", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://evil.example/final", false)]
    [InlineData("http://localhost/final", true)]
    public async Task Credential_headers_only_survive_same_origin_redirects(string target, bool expectCredentials)
    {
        var stub = new RedirectingStub(new Uri(target));
        var services = new ServiceCollection();
        services.AddCairnClient(o =>
            {
                o.BaseAddress = new Uri("http://localhost");
                o.AllowLink = _ => true;
            })
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .ConfigureHttpClient(http =>
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer token");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "session=1");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Proxy-Authorization", "Basic x");
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Keep", "yes");
            });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.GetAsync<JsonElement>("/start");

        Assert.True(result.IsSuccess);
        Assert.NotNull(stub.FinalHeaders);
        Assert.True(stub.FinalHeaders!.ContainsKey("X-Keep"));
        Assert.Equal(expectCredentials, stub.FinalHeaders.ContainsKey("Authorization"));
        Assert.Equal(expectCredentials, stub.FinalHeaders.ContainsKey("Cookie"));
        Assert.Equal(expectCredentials, stub.FinalHeaders.ContainsKey("Proxy-Authorization"));
    }

    [Fact]
    public async Task A_body_over_the_configured_buffer_cap_throws_instead_of_buffering_it()
    {
        var body = $$"""{ "name": "{{new string('a', 4096)}}" }""";
        var http = new HttpClient(new StubHandler(body, "application/json"))
        {
            BaseAddress = new Uri("http://localhost"),
            MaxResponseContentBufferSize = 1024,
        };
        var client = new CairnClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<JsonElement>("/big"));
    }

    [Fact]
    public async Task A_body_that_cannot_bind_reports_the_binding_not_invalid_json()
    {
        var client = NewClient("""{ "id": "not a number" }""", "application/json");

        var result = await client.GetAsync<BindThing>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal("The response body could not be bound to 'BindThing'.", result.Problem!.Title);
        Assert.Contains("valid JSON", result.Problem.Detail);
    }

    [Fact]
    public async Task An_unbindable_target_type_stays_inside_the_result_contract()
    {
        var client = NewClient("{}", "application/json");

        // System.Text.Json refuses System.Type with NotSupportedException; it must surface as a failed
        // result, not escape the documented no-throw contract.
        var result = await client.GetAsync<Type>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal("The response body could not be bound to 'Type'.", result.Problem!.Title);
    }

    private static CairnClient NewClient(string body, string contentType)
    {
        var http = new HttpClient(new StubHandler(body, contentType)) { BaseAddress = new Uri("http://localhost") };
        return new CairnClient(http);
    }

    private sealed record BindThing(int Id);

    private sealed class StubHandler(string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }

    // Mimics a primary handler that followed a redirect internally: the response reports a final URI
    // different from the one that was sent (as HttpClientHandler does after auto-redirecting).
    private sealed class AutoFollowingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri("http://internal.example/secret");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RedirectingStub(Uri target) : HttpMessageHandler
    {
        public Dictionary<string, string>? FinalHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = target;
                return Task.FromResult(redirect);
            }

            FinalHeaders = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
