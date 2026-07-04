using System.Net;

namespace Cairn.Testing;

/// <summary>Entry point for fluent assertions over an <see cref="HttpResponseMessage"/>.</summary>
public static class HttpResponseAssertionsExtensions
{
    /// <summary>Returns fluent assertions for the HTTP response's status line and headers.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static HttpResponseAssertions Should(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new HttpResponseAssertions(response);
    }
}

/// <summary>
/// Fluent assertions over the transport-level facts of an <see cref="HttpResponseMessage"/> — its status code,
/// <c>Content-Type</c>, and <c>ETag</c> — complementing the hypermedia assertions reached via
/// <see cref="HypermediaResponseAssertionsExtensions.Should(HypermediaResponse)"/>.
/// </summary>
public sealed class HttpResponseAssertions
{
    private readonly HttpResponseMessage _response;

    internal HttpResponseAssertions(HttpResponseMessage response) => _response = response;

    /// <summary>Continues a chain of assertions.</summary>
    public HttpResponseAssertions And => this;

    /// <summary>Asserts the response has the given status code.</summary>
    public HttpResponseAssertions HaveStatusCode(HttpStatusCode statusCode)
    {
        CairnAssert.That(
            _response.StatusCode == statusCode,
            $"Expected the response to have status code {(int)statusCode} ({statusCode}), but it is {(int)_response.StatusCode} ({_response.StatusCode}).");
        return this;
    }

    /// <summary>
    /// Asserts the response's <c>Content-Type</c> media type equals <paramref name="mediaType"/>. Parameters such
    /// as <c>charset</c> are ignored and the comparison is case-insensitive, so <c>application/hal+json</c> matches
    /// a header of <c>application/hal+json; charset=utf-8</c>.
    /// </summary>
    public HttpResponseAssertions HaveContentType(string mediaType)
    {
        var actual = _response.Content.Headers.ContentType?.MediaType;
        CairnAssert.That(
            string.Equals(actual, mediaType, StringComparison.OrdinalIgnoreCase),
            $"Expected the response's content type to be '{mediaType}', but it is '{actual ?? "none"}'.");
        return this;
    }

    /// <summary>Asserts the response carries an <c>ETag</c> header.</summary>
    public HttpResponseAssertions HaveETag()
    {
        CairnAssert.That(
            _response.Headers.ETag is not null,
            "Expected the response to carry an ETag header, but it does not.");
        return this;
    }

    /// <summary>
    /// Asserts the response's <c>ETag</c> header equals <paramref name="etag"/> — quotes and any weak <c>W/</c>
    /// prefix included, so the expectation reads exactly as the tag on the wire (for example, <c>"v1"</c> or
    /// <c>W/"v1"</c>).
    /// </summary>
    public HttpResponseAssertions HaveETag(string etag)
    {
        var actual = _response.Headers.ETag?.ToString();
        CairnAssert.That(
            actual == etag,
            $"Expected the response to have ETag '{etag}', but it is '{actual ?? "none"}'.");
        return this;
    }
}
