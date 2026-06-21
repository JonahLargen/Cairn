using Cairn.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace Cairn.Sample.Api;

/// <summary>A plain customer DTO — Cairn attaches links without modifying it.</summary>
public record CustomerDto(int Id, string Name);

/// <summary>Declares the hypermedia for <see cref="CustomerDto"/> — the same config drives controllers and minimal APIs.</summary>
public sealed class CustomerLinks : LinkConfig<CustomerDto>
{
    public override void Configure(ILinkBuilder<CustomerDto> builder)
        => builder.Self(customer => LinkTarget.Route("GetCustomerById", new { id = customer.Id }));
}

/// <summary>An MVC controller — the same opt-in model as minimal APIs, via <c>[CairnLinks]</c>.</summary>
[ApiController]
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    [HttpGet("{id:int}", Name = "GetCustomerById")]
    [CairnLinks]
    public CustomerDto Get(int id) => new(id, "Acme Corp");
}
