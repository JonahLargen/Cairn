namespace Cairn.Sample.Api;

/// <summary>
/// A tiny in-memory data store so the sample's affordances actually round-trip: placing an order adds one,
/// cancelling flips its state, and the explorer sees the change on the next navigation. Registered as a
/// singleton; guarded by a lock because the dev server handles requests concurrently.
/// </summary>
public sealed class Store
{
    private readonly Lock _gate = new();
    private readonly List<CustomerDto> _customers =
    [
        new(1, "Acme Corp"),
        new(2, "Globex"),
    ];
    private readonly List<OrderDto> _orders =
    [
        new(42, CustomerId: 1, Quantity: 3, Speed: ShippingSpeed.Standard, Status: OrderStatus.Pending),
        new(7, CustomerId: 2, Quantity: 1, Speed: ShippingSpeed.Express, Status: OrderStatus.Shipped),
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

    /// <summary>Every customer.</summary>
    public IReadOnlyList<CustomerDto> Customers()
    {
        lock (_gate)
        {
            return _customers.ToList();
        }
    }

    /// <summary>The customer with <paramref name="id"/>, or <see langword="null"/> if none.</summary>
    public CustomerDto? Customer(int id)
    {
        lock (_gate)
        {
            return _customers.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>Places a new order in the <see cref="OrderStatus.Pending"/> state and returns it.</summary>
    public OrderDto Place(int customerId, int quantity, ShippingSpeed speed)
    {
        lock (_gate)
        {
            var order = new OrderDto(_nextOrderId++, customerId, quantity, speed, OrderStatus.Pending);
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
