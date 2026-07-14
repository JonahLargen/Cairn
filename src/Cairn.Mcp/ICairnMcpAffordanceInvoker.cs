using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cairn.Mcp;

/// <summary>
/// Executes an affordance an MCP tool call has already resolved and gate-checked. The default implementation
/// sends an HTTP request to the affordance's own target URL — the same endpoint a hypermedia client would
/// call — so endpoint-level validation and authorization stay authoritative. Replace it to invoke actions
/// in-process or over a different transport.
/// </summary>
public interface ICairnMcpAffordanceInvoker
{
    /// <summary>Executes <paramref name="call"/> and reports the outcome.</summary>
    Task<CairnMcpAffordanceResult> InvokeAsync(CairnMcpAffordanceCall call, CancellationToken cancellationToken = default);
}

/// <summary>An affordance invocation requested through an MCP tool call.</summary>
public sealed record CairnMcpAffordanceCall
{
    /// <summary>The affordance as the link engine resolved it for the loaded resource — href, method, and input metadata.</summary>
    public required Affordance Affordance { get; init; }

    /// <summary>The MCP request's <see cref="HttpContext"/>, carrying the caller's identity and the request's base address.</summary>
    public required HttpContext HttpContext { get; init; }

    /// <summary>The tool-call arguments destined for the action's input (the <c>id</c> argument already removed).</summary>
    public required IReadOnlyDictionary<string, JsonElement> Arguments { get; init; }
}

/// <summary>The outcome of executing an affordance.</summary>
/// <param name="StatusCode">The HTTP status code the action's endpoint answered with.</param>
/// <param name="Content">The response body, or <see langword="null"/>/empty when the endpoint returned none.</param>
/// <param name="ContentType">The response's content type, if any.</param>
public sealed record CairnMcpAffordanceResult(int StatusCode, string? Content, string? ContentType)
{
    /// <summary>Whether the status code indicates success (2xx).</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
