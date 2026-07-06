namespace CairnApi;

/// <summary>
/// A tiny in-memory data store so the affordances actually round-trip: placing an order adds one,
/// cancelling flips its state, and the next navigation sees the change. Registered as a singleton;
/// guarded by a lock because the dev server handles requests concurrently. Swap it for a real
/// repository when you wire this up to a database.
/// </summary>
public sealed class Store
{
    private readonly object _gate = new();
    private readonly List<OrderDto> _orders =
    [
        new(42, Quantity: 3, Speed: ShippingSpeed.Standard, Status: OrderStatus.Pending),
        new(7, Quantity: 1, Speed: ShippingSpeed.Express, Status: OrderStatus.Shipped),
    ];
    private int _nextOrderId = 100;

    /// <summary>Every order, newest first.</summary>
    public IReadOnlyList<OrderDto> Orders()
    {
        lock (_gate)
        {
            return _orders.OrderByDescending(o => o.Id).ToList();
        }
    }

    /// <summary>The order with <paramref name="id"/>, or <see langword="null"/> if none.</summary>
    public OrderDto? Order(int id)
    {
        lock (_gate)
        {
            return _orders.FirstOrDefault(o => o.Id == id);
        }
    }

    /// <summary>Places a new order in the <see cref="OrderStatus.Pending"/> state and returns it.</summary>
    public OrderDto Place(int quantity, ShippingSpeed speed)
    {
        lock (_gate)
        {
            var order = new OrderDto(_nextOrderId++, quantity, speed, OrderStatus.Pending);
            _orders.Add(order);
            return order;
        }
    }

    /// <summary>
    /// Cancels a pending order. Returns <see langword="true"/> when an order was found and cancelled,
    /// <see langword="false"/> when it does not exist or was not pending.
    /// </summary>
    public bool Cancel(int id)
    {
        lock (_gate)
        {
            var index = _orders.FindIndex(o => o.Id == id);
            if (index < 0 || _orders[index].Status != OrderStatus.Pending)
            {
                return false;
            }

            _orders[index] = _orders[index] with { Status = OrderStatus.Cancelled };
            return true;
        }
    }
}
