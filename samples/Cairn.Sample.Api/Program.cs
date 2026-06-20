using Cairn.AspNetCore;
using Cairn.Sample.Api;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCairn(options => options.AddLinks(new OrderLinks()));

var app = builder.Build();

var orders = app.MapGroup("/orders");

// A plain record gains a self link and a state-conditional 'cancel' affordance.
orders.MapGet("/{id:int}", (int id) => TypedResults.Ok(new OrderDto(id, OrderStatus.Pending)))
    .WithName("GetOrderById")
    .WithLinks<OrderDto>();

// A Results<,> union — links are attached to the inner value when present.
orders.MapGet("/find/{id:int}", Results<Ok<OrderDto>, NotFound> (int id) =>
        id > 0 ? TypedResults.Ok(new OrderDto(id, OrderStatus.Shipped)) : TypedResults.NotFound())
    .WithLinks<OrderDto>();

// The target the 'cancel' affordance points at.
orders.MapPost("/{id:int}/cancel", (int id) => TypedResults.NoContent())
    .WithName("CancelOrder");

app.Run();
