using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// One-time startup diagnostics for configurations that work but are easy to get subtly wrong in production.
/// Today: absolute link URLs derived from the incoming request's <c>Host</c> header with nothing correcting
/// it — behind a proxy the links leak internal hostnames, and the header itself is client-controlled, so a
/// crafted <c>Host</c> is reflected into every generated link.
/// </summary>
internal sealed class CairnStartupWarnings(IServiceProvider services, CairnOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.UrlStyle == LinkUrlStyle.Absolute
            && options.PublicBaseUri is null
            && !ForwardedHeadersConfigured()
            && services.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            loggerFactory.CreateLogger("Cairn.AspNetCore").LogWarning(
                "Cairn: UrlStyle is Absolute, so link URLs are built from the incoming request's Host header, and neither ForwardedHeadersOptions nor CairnOptions.PublicBaseUri is configured to correct it. " +
                "Behind a proxy or gateway the generated links will carry the internal hostname, and the Host header is client-controlled. " +
                "Configure forwarded headers (services.Configure<ForwardedHeadersOptions>(...) with app.UseForwardedHeaders()), set CairnOptions.PublicBaseUri, or switch to LinkUrlStyle.PathRelative.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Forwarded headers configured through the options system — either explicitly or by the
    // ASPNETCORE_FORWARDEDHEADERS_ENABLED switch — restore the public scheme/host before Cairn reads them.
    // Options passed inline to app.UseForwardedHeaders(new ...) can't be observed from here; that setup
    // still warns, and PublicBaseUri (or DI-configured options) silences it.
    private bool ForwardedHeadersConfigured()
        => services.GetService<IOptions<ForwardedHeadersOptions>>()?.Value.ForwardedHeaders is { } forwarded
            && forwarded != ForwardedHeaders.None;
}
