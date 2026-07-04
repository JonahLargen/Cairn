using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

// Redirect-handler branches: method rewriting per status (301/302/303 vs 307/308), body-preserving
// redirects that cannot be replayed, and the redirect-visibility guard's tolerance for responses that
// carry no request message or a relative final URI.
public class LinkPolicyRedirectBranchTests
{
    [Fact]
    public async Task A_307_of_a_request_with_a_body_surfaces_the_3xx_instead_of_replaying()
    {
        var stub = new RedirectingStub(HttpStatusCode.TemporaryRedirect);
        await using var provider = NewPolicyProvider(stub);
        var client = provider.GetRequiredService<CairnClient>();

        // The original content stream may already be consumed, so a body-preserving redirect cannot be
        // honored — the caller sees the 307 rather than a broken re-send.
        var result = await client.InvokeAsync(new Affordance("act", "/start", "POST"), body: new { note = "hi" });

        Assert.False(result.IsSuccess);
        Assert.Equal(307, result.Status);
        Assert.Null(stub.FinalMethod);   // the redirect was never followed
    }

    [Fact]
    public async Task A_307_of_a_bodiless_POST_is_followed_preserving_the_method()
    {
        var stub = new RedirectingStub(HttpStatusCode.TemporaryRedirect);
        await using var provider = NewPolicyProvider(stub);
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.InvokeAsync(new Affordance("act", "/start", "POST"));

        Assert.True(result.IsSuccess);
        Assert.Equal("POST", stub.FinalMethod);
    }

    [Fact]
    public async Task A_307_of_a_HEAD_request_with_a_body_is_followed()
    {
        var stub = new RedirectingStub(HttpStatusCode.TemporaryRedirect);
        await using var provider = NewPolicyProvider(stub);
        var client = provider.GetRequiredService<CairnClient>();

        // HEAD is safe to re-issue even though a body was attached: the follow-up carries none.
        var result = await client.InvokeAsync(new Affordance("act", "/start", "HEAD"), body: new { note = "hi" });

        Assert.True(result.IsSuccess);
        Assert.Equal("HEAD", stub.FinalMethod);
    }

    [Theory]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    public async Task A_redirected_POST_with_a_body_is_re_fetched_with_GET(HttpStatusCode status)
    {
        var stub = new RedirectingStub(status);
        await using var provider = NewPolicyProvider(stub);
        var client = provider.GetRequiredService<CairnClient>();

        // 303 always rewrites to GET; 301/302 conventionally rewrite POST — the body is dropped with it.
        var result = await client.InvokeAsync(new Affordance("act", "/start", "POST"), body: new { note = "hi" });

        Assert.True(result.IsSuccess);
        Assert.Equal("GET", stub.FinalMethod);
    }

    [Fact]
    public async Task A_308_of_a_GET_is_followed_with_GET()
    {
        var stub = new RedirectingStub(HttpStatusCode.PermanentRedirect);
        await using var provider = NewPolicyProvider(stub);
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.GetAsync<JsonElement>("/start");

        Assert.True(result.IsSuccess);
        Assert.Equal("GET", stub.FinalMethod);
    }

    [Fact]
    public async Task A_response_without_a_request_message_passes_the_visibility_guard()
    {
        // A stub (or cache) that never sets RequestMessage leaves the guard nothing to compare — it must
        // let the response through rather than dereference null.
        await using var provider = NewPolicyProvider(new BareResponseStub(request => null));
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.GetAsync<JsonElement>("/start");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_response_whose_request_lost_its_uri_passes_the_visibility_guard()
    {
        await using var provider = NewPolicyProvider(new BareResponseStub(request =>
        {
            request.RequestUri = null;
            return request;
        }));
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.GetAsync<JsonElement>("/start");

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("/elsewhere")]
    [InlineData("/elsewhere?leftover=1")]
    public async Task A_relative_final_uri_on_another_path_still_trips_the_visibility_guard(string finalUri)
    {
        // An inner handler rewriting the request to a relative URI on a different path is still a hop the
        // policy never inspected; only the query (if any) is ignored by the comparison.
        await using var provider = NewPolicyProvider(new BareResponseStub(request =>
        {
            request.RequestUri = new Uri(finalUri, UriKind.Relative);
            return request;
        }));
        var client = provider.GetRequiredService<CairnClient>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<JsonElement>("/start"));

        Assert.Contains("redirect", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider NewPolicyProvider(HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = _ => true;
        }).ConfigurePrimaryHttpMessageHandler(() => primary);

        return services.BuildServiceProvider();
    }

    // Redirects the first request to /final with the configured status, then records the follow-up's method.
    private sealed class RedirectingStub(HttpStatusCode status) : HttpMessageHandler
    {
        public string? FinalMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                var redirect = new HttpResponseMessage(status) { RequestMessage = request };
                redirect.Headers.Location = new Uri("http://localhost/final");
                return Task.FromResult(redirect);
            }

            FinalMethod = request.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    // Returns 200 with whatever RequestMessage the mutator supplies (possibly none, or one whose URI was
    // cleared or made relative).
    private sealed class BareResponseStub(Func<HttpRequestMessage, HttpRequestMessage?> mutate) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = mutate(request),
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}
