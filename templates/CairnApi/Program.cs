using System.Text.Json.Serialization;
using Cairn.AspNetCore;
#if (explorer)
using Cairn.AspNetCore.Explorer;
#endif
#if (openapi)
using Cairn.Swashbuckle;
#endif
using Microsoft.AspNetCore.Http.HttpResults;
using CairnApi;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums by name (Pending, Standard, …) rather than as integers, so responses — and the
// HAL-FORMS options lists derived from them — read as words.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Register Cairn and every link configuration. Registering makes hypermedia *available*; a response
// only gains it when its endpoint opts in with .WithLinks(). Every other endpoint is byte-for-byte
// unchanged, so Cairn is safe to add one endpoint at a time.
builder.Services.AddCairn(options =>
{
    options.AddLinks(new RootLinks());
    options.AddLinks(new OrderLinks());
    options.AddLinks(new OrdersLinks());
});

builder.Services.AddSingleton<Store>();

#if (openapi)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.AddCairnHypermedia());
#endif

var app = builder.Build();

#if (openapi)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
#endif

#if (explorer)
// The browsable HAL Explorer, mounted at /explorer (Development only by default). Open it, start at
// the root, and navigate the whole API by following links and running the forms.
app.UseCairnExplorer(options => options.Title = "CairnApi Explorer");
#endif

// The API entry point — a home resource whose only job is to link onward. A hypermedia client (and
// the explorer) starts here and discovers the rest of the API by following links.
app.MapGet("/", () => TypedResults.Ok(new RootDto()))
    .WithName("GetRoot")
    .WithLinks();

var orders = app.MapGroup("/orders");

// The orders collection — items surface in _embedded.orders, and a 'create' form is advertised.
orders.MapGet("/", (Store store) => TypedResults.Ok(new OrdersResource(store.Orders())))
    .WithName("GetOrders")
    .WithLinks();

// A single order — self, a link back to the collection, and a state-conditional 'cancel' affordance
// that appears only while the order is Pending.
orders.MapGet("/{id:int}", Results<Ok<OrderDto>, NotFound> (int id, Store store) =>
        store.Order(id) is { } order ? TypedResults.Ok(order) : TypedResults.NotFound())
    .WithName("GetOrderById")
    .WithLinks();

// Place an order — returns 201 with a Location the client (or explorer) follows to the new resource.
orders.MapPost("/", (CreateOrderInput input, Store store, LinkGenerator links, HttpContext http) =>
    {
        var order = store.Place(input.Quantity, input.Speed);
        var location = links.GetPathByName(http, "GetOrderById", new { id = order.Id });
        return TypedResults.Created(location, order);
    })
    .WithName("CreateOrder");

// The target the 'cancel' affordance points at — 204 on success, so a re-fetch shows the order
// flipped to Cancelled with the affordance gone.
orders.MapPost("/{id:int}/cancel", Results<NoContent, NotFound> (int id, CancelOrderInput input, Store store) =>
        store.Cancel(id) ? TypedResults.NoContent() : TypedResults.NotFound())
    .WithName("CancelOrder");

app.Run();
