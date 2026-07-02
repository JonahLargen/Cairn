using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

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
            builder
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler(() => new LinkPolicyRedirectHandler(allowLink));
        }

        return builder;
    }
}
