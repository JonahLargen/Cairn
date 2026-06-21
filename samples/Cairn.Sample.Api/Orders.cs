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
        // Routes.* is generated from the named endpoints — compile-checked, no magic strings.
        builder.Self(order => Routes.GetOrderById(order.Id));

        builder.Affordance("cancel", order => Routes.CancelOrder(order.Id))
            .Method("POST")
            .When(order => order.Status == OrderStatus.Pending);
    }
}
