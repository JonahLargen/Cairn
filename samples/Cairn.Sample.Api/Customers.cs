using System.Text.Json.Serialization;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace Cairn.Sample.Api;

/// <summary>A plain customer DTO — Cairn attaches links without modifying it.</summary>
public record CustomerDto(int Id, string Name);

/// <summary>
/// The customers collection resource. Items live in <c>_embedded.customers</c> (via <see cref="CustomersLinks"/>).
/// </summary>
public sealed record CustomersResource([property: JsonIgnore] IReadOnlyList<CustomerDto> Customers);

/// <summary>Declares the hypermedia for <see cref="CustomerDto"/> — the same config drives controllers and minimal APIs.</summary>
public sealed class CustomerLinks : LinkConfig<CustomerDto>
{
    // Routes.GetCustomerById is generated from the controller's [HttpGet(Name = "GetCustomerById")] — same
    // compile-checked catalog as minimal-API routes.
    public override void Configure(ILinkBuilder<CustomerDto> builder)
    {
        builder.Self(customer => Routes.GetCustomerById(customer.Id));
        builder.Link("orders", _ => Routes.GetOrders()).Title("All orders");
    }
}

/// <summary>Declares the hypermedia for the customers collection.</summary>
public sealed class CustomersLinks : LinkConfig<CustomersResource>
{
    public override void Configure(ILinkBuilder<CustomersResource> builder)
    {
        builder.Self(_ => Routes.GetCustomers());
        builder.Link("orders", _ => Routes.GetOrders()).Title("Orders");
        builder.EmbedMany("customers", collection => collection.Customers);
    }
}

/// <summary>An MVC controller — the same opt-in model as minimal APIs, via <c>[CairnLinks]</c>.</summary>
[ApiController]
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly Store _store;

    public CustomersController(Store store) => _store = store;

    [HttpGet("{id:int}", Name = "GetCustomerById")]
    [CairnLinks]
    public ActionResult<CustomerDto> Get(int id)
        => _store.Customer(id) is { } customer ? customer : NotFound();
}
