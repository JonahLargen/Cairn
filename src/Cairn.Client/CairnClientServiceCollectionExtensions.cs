using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Cairn.Client;

/// <summary>Options for registering a <see cref="CairnClient"/> with dependency injection.</summary>
public sealed class CairnClientOptions
{
    /// <summary>The base address requests are made against. Required to follow relative links.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The JSON options used to deserialize resource bodies.</summary>
    public JsonSerializerOptions? JsonOptions { get; set; }

    /// <summary>A policy gating which absolute link targets may be followed (SSRF protection). Return <see langword="false"/> to reject.</summary>
    public Func<Uri, bool>? AllowLink { get; set; }
}

/// <summary>Dependency-injection extensions for <see cref="CairnClient"/>.</summary>
public static class CairnClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CairnClient"/> as a typed <see cref="System.Net.Http.HttpClient"/> over
    /// <c>IHttpClientFactory</c>, so it participates in DI, handlers, and resilience. Returns the
    /// <see cref="IHttpClientBuilder"/> for further configuration (e.g. <c>AddStandardResilienceHandler</c>).
    /// When <see cref="CairnClientOptions.AllowLink"/> is set, handler-level auto-redirect is disabled and
    /// redirects are followed by a policy-enforcing handler instead, so every hop — not just the first
    /// request — must satisfy the policy.
    /// </summary>
    public static IHttpClientBuilder AddCairnClient(this IServiceCollection services, Action<CairnClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CairnClientOptions();
        configure?.Invoke(options);

        var builder = services
            .AddHttpClient<CairnClient>(http =>
            {
                if (options.BaseAddress is not null)
                {
                    http.BaseAddress = options.BaseAddress;
                }
            })
            .AddTypedClient((http, _) => new CairnClient(http, options.JsonOptions, options.AllowLink));

        if (options.AllowLink is { } allowLink)
        {
            builder.AddHttpMessageHandler(() => new LinkPolicyRedirectHandler(allowLink));

            // Disable auto-redirect on whatever primary handler the pipeline ends up with, rather than
            // registering a specific one: a consumer replacing the primary handler (proxy, mTLS, ...) must
            // not silently re-enable in-handler redirects the policy can never see. PostConfigure appends
            // this action after every consumer-registered configuration, so it observes the final handler;
            // a handler this can't recognize is caught at runtime by LinkPolicyRedirectHandler, which fails
            // loudly when a response arrives through a redirect it did not inspect.
            services.PostConfigure<HttpClientFactoryOptions>(builder.Name, factoryOptions =>
                factoryOptions.HttpMessageHandlerBuilderActions.Add(b => DisableAutoRedirect(b.PrimaryHandler)));
        }

        return builder;
    }

    private static void DisableAutoRedirect(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        if (handler is HttpClientHandler httpClientHandler)
        {
            httpClientHandler.AllowAutoRedirect = false;
        }
        else if (handler is SocketsHttpHandler socketsHttpHandler)
        {
            socketsHttpHandler.AllowAutoRedirect = false;
        }
    }
}
