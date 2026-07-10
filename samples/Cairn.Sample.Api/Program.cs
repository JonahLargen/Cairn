using System.Text.Json.Serialization;
using Cairn.AspNetCore;
using Cairn.AspNetCore.Explorer;
using Cairn.Sample.Api;
using Cairn.Mcp;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<Store>();

// Serialize enums by name (Pending, Standard, …) rather than as integers, so responses — and the explorer's
// state pills and options lists — read as words.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCairn(options =>
{
    options.AddLinks(new RootLinks());
    options.AddLinks(new OrderLinks());
    options.AddLinks(new OrdersLinks());
    options.AddLinks(new CustomerLinks());
    options.AddLinks(new CustomersLinks());
});

// The MCP surface for AI agents: each affordance declared above becomes a tool (order_cancel, orders_create),
// gated by the same state and authorization rules as the hypermedia, plus order_get/orders_get for discovery.
// Point an MCP client at /mcp to try it.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithCairnAffordances(mcp =>
    {
        mcp.AddResource<OrderDto>("order", (id, services, _) =>
            new ValueTask<OrderDto?>(int.TryParse(id, out var orderId) ? services.GetRequiredService<Store>().Order(orderId) : null));
        mcp.AddResource<OrdersResource>("orders", (services, _) =>
            new ValueTask<OrdersResource?>(new OrdersResource(services.GetRequiredService<Store>().Orders())));
    });

var app = builder.Build();

// The browsable HAL Explorer, mounted at /explorer. It is served in Development only by default; open it and
// start from the API root, then navigate entirely by following links and running the forms.
app.UseCairnExplorer(options => options.Title = "Cairn Sample API Explorer");

// Controllers opt in the same way — see CustomersController and its [CairnLinks].
app.MapControllers();

// The API entry point — links onward to the collections. This is where the explorer opens.
app.MapGet("/", () => TypedResults.Ok(new RootDto()))
    .WithName("GetRoot")
    .WithLinks();

var orders = app.MapGroup("/orders");

// The orders collection — items surface in _embedded.orders, and a 'create' form is advertised.
orders.MapGet("/", (Store store) => TypedResults.Ok(new OrdersResource(store.Orders())))
    .WithName("GetOrders")
    .WithLinks();

// A single order — self, a cross-link to its customer, and a state-conditional 'cancel' affordance.
orders.MapGet("/{id:int}", Results<Ok<OrderDto>, NotFound> (int id, Store store) =>
        store.Order(id) is { } order ? TypedResults.Ok(order) : TypedResults.NotFound())
    .WithName("GetOrderById")
    .WithLinks();

// Place an order — returns 201 with a Location the explorer follows to the new resource.
orders.MapPost("/", (CreateOrderInput input, Store store, LinkGenerator links, HttpContext http) =>
    {
        var order = store.Place(input.CustomerId, input.Quantity, input.Speed);
        var location = links.GetPathByName(http, "GetOrderById", new { id = order.Id });
        return TypedResults.Created(location, order);
    })
    .WithName("CreateOrder");

// The target the 'cancel' affordance points at — 204 on success, so the explorer reloads the order and shows
// it flipped to Cancelled with the affordance gone.
orders.MapPost("/{id:int}/cancel", Results<NoContent, NotFound> (int id, CancelOrderInput input, Store store) =>
        store.Cancel(id) ? TypedResults.NoContent() : TypedResults.NotFound())
    .WithName("CancelOrder");

// The customers collection — items surface in _embedded.customers.
app.MapGet("/customers", (Store store) => TypedResults.Ok(new CustomersResource(store.Customers())))
    .WithName("GetCustomers")
    .WithLinks();

// The Model Context Protocol endpoint — agents list and call the affordance tools here.
app.MapMcp("/mcp");

app.Run();
