using System.Text.Json.Serialization;
using Cairn;

namespace Cairn.Benchmarks;

public sealed record OrderDto(int Id, int Status);

public sealed record UnconfiguredDto(int Id);

/// <summary>Route-based configuration: every self/cancel href goes through <c>LinkGenerator</c>.</summary>
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("BenchGetOrder", new { id = order.Id }));
        builder.Link("collection", _ => LinkTarget.Uri("/orders"));
        builder.Affordance("cancel", order => LinkTarget.Route("BenchCancelOrder", new { id = order.Id }))
            .Post()
            .When(order => order.Status == 1);
    }
}

/// <summary>Same shape as <see cref="OrderDto"/>, configured with explicit URIs instead of named routes.</summary>
public sealed record UriOrderDto(int Id, int Status);

/// <summary>
/// The escape hatch for hot collection endpoints: <c>LinkTarget.Uri</c> skips <c>LinkGenerator</c> —
/// per-link route-value binding and URL generation, the dominant per-item cost — at the price of
/// hand-maintaining the paths (and emitting them relative here).
/// </summary>
public sealed class UriOrderLinks : LinkConfig<UriOrderDto>
{
    public override void Configure(ILinkBuilder<UriOrderDto> builder)
    {
        builder.Self(order => LinkTarget.Uri($"/orders/{order.Id}"));
        builder.Link("collection", _ => LinkTarget.Uri("/orders"));
        builder.Affordance("cancel", order => LinkTarget.Uri($"/orders/{order.Id}/cancel"))
            .Post()
            .When(order => order.Status == 1);
    }
}

// The hand-rolled shapes below are what a developer writes *instead of* using Cairn: link objects declared
// on the DTO and built inline in the handler. They are the real-world alternative the "WithLinks" benchmarks
// should be judged against — nobody chooses between links and no links.

public sealed record HandRolledLink(string Href);

public sealed record HandRolledAction(string Href, string Method);

public sealed record HandRolledOrderDto(int Id, int Status)
{
    [JsonPropertyName("_links")]
    public required Dictionary<string, HandRolledLink> Links { get; init; }

    [JsonPropertyName("_actions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, HandRolledAction>? Actions { get; init; }
}

public sealed record HandRolledPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    [property: JsonPropertyName("_links")] Dictionary<string, HandRolledLink> Links)
{
    // Mirrors PagedResource<T> so the hand-rolled and WithLinks payloads match field for field.
    public int TotalPages => PageSize <= 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
}
