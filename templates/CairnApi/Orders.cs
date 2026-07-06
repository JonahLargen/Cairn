using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cairn;

namespace CairnApi;

/// <summary>The lifecycle state of an order.</summary>
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled,
}

/// <summary>How fast an order ships — surfaced as a HAL-FORMS options list on the "place an order" form.</summary>
public enum ShippingSpeed
{
    Standard,
    Express,
    Overnight,
}

/// <summary>A plain order DTO — Cairn attaches links without modifying it.</summary>
public record OrderDto(int Id, int Quantity, ShippingSpeed Speed, OrderStatus Status);

/// <summary>
/// The orders collection resource. Its items live in <c>_embedded.orders</c> (via <see cref="OrdersLinks"/>),
/// so <c>[JsonIgnore]</c> keeps them from also serializing as a plain body property.
/// </summary>
public sealed record OrdersResource([property: JsonIgnore] IReadOnlyList<OrderDto> Orders);

/// <summary>The input the "place an order" affordance accepts — its properties become HAL-FORMS fields.</summary>
public sealed class CreateOrderInput
{
    [Range(1, 100)]
    public int Quantity { get; set; } = 1;

    [Display(Name = "Shipping speed")]
    public ShippingSpeed Speed { get; set; }
}

/// <summary>The input the "cancel" affordance accepts.</summary>
public sealed class CancelOrderInput
{
    [Required]
    [Display(Prompt = "Why is this order being cancelled?")]
    public string Reason { get; set; } = "";
}

/// <summary>Declares the hypermedia for <see cref="OrderDto"/>.</summary>
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => Routes.GetOrderById(order.Id));
        builder.Link("collection", _ => Routes.GetOrders()).Title("All orders");

        // A state-conditional action with an input form: only pending orders can be cancelled, so the
        // affordance is present on a Pending order and absent once it ships or is cancelled.
        builder.Affordance("cancel", order => Routes.CancelOrder(order.Id))
            .Post()
            .Accepts<CancelOrderInput>()
            .Title("Cancel this order")
            .When(order => order.Status == OrderStatus.Pending);
    }
}

/// <summary>Declares the hypermedia for the orders collection.</summary>
public sealed class OrdersLinks : LinkConfig<OrdersResource>
{
    public override void Configure(ILinkBuilder<OrdersResource> builder)
    {
        builder.Self(_ => Routes.GetOrders());

        // Each embedded order is decorated with its own OrderLinks (self, collection, cancel).
        builder.EmbedMany("orders", collection => collection.Orders);

        // The collection's affordance — a form for placing a new order.
        builder.Affordance("create", _ => Routes.CreateOrder())
            .Post()
            .Accepts<CreateOrderInput>()
            .Title("Place an order")
            .AsDefault();
    }
}
