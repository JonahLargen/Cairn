using Cairn;

namespace CairnApi;

/// <summary>
/// The API's entry point — a home resource whose only job is to link onward. Keeping a real root
/// resource means clients (and the HAL Explorer) can start at <c>/</c> and reach everything by
/// following links, never hard-coding URLs.
/// </summary>
public sealed record RootDto(string Api = "CairnApi", string Version = "1.0");

/// <summary>Declares the hypermedia for the API entry point.</summary>
public sealed class RootLinks : LinkConfig<RootDto>
{
    public override void Configure(ILinkBuilder<RootDto> builder)
    {
        // Routes.* is generated from the named endpoints — compile-checked, no magic strings.
        builder.Self(_ => Routes.GetRoot());
        builder.Link("orders", _ => Routes.GetOrders()).Title("Orders collection");
    }
}
