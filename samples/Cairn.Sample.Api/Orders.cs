namespace Cairn.Sample.Api;

/// <summary>The lifecycle state of an order.</summary>
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled,
}

/// <summary>A plain order DTO — Cairn attaches links without modifying it.</summary>
public record OrderDto(int Id, OrderStatus Status);

/// <summary>Declares the hypermedia for <see cref="OrderDto"/>.</summary>
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));

        builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
            .Method("POST")
            .When(order => order.Status == OrderStatus.Pending);
    }
}
