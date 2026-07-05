using Cairn.AspNetCore;

namespace Cairn.Sample.Api;

/// <summary>
/// The API's entry point — a home resource whose only job is to link onward. A hypermedia client (and the HAL
/// Explorer) starts here and discovers the rest of the API by following links, never hard-coding URLs.
/// </summary>
public sealed record RootDto(string Api = "Cairn Sample API", string Version = "1.0");

/// <summary>Declares the hypermedia for the API entry point.</summary>
public sealed class RootLinks : LinkConfig<RootDto>
{
    public override void Configure(ILinkBuilder<RootDto> builder)
    {
        builder.Self(_ => Routes.GetRoot());
        builder.Link("orders", _ => Routes.GetOrders()).Title("Orders collection");
        builder.Link("customers", _ => Routes.GetCustomers()).Title("Customers collection");
    }
}
