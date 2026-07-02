namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata carrying an endpoint's deprecation headers, precomputed by
/// <c>WithDeprecation</c> and emitted by <see cref="CairnHeadersMiddleware"/>. Metadata (rather than an
/// endpoint filter) reaches every endpoint type — MVC controller actions as well as minimal-API handlers.
/// </summary>
internal sealed class DeprecationMetadata(string deprecation, string? sunset, string? link)
{
    /// <summary>The <c>Deprecation</c> header value (RFC 9745): <c>@unix-seconds</c> or <c>true</c>.</summary>
    public string Deprecation { get; } = deprecation;

    /// <summary>The <c>Sunset</c> header value (RFC 8594), an HTTP-date, if any.</summary>
    public string? Sunset { get; } = sunset;

    /// <summary>The <c>Link</c> header value (<c>&lt;url&gt;; rel="deprecation"</c>), if any.</summary>
    public string? Link { get; } = link;
}
