# MCP server for AI agents

`Cairn.Mcp` exposes the affordances your link configurations declare as [Model Context Protocol](https://modelcontextprotocol.io) tools, so AI agents can discover and invoke your API's actions under exactly the gates a hypermedia response applies. It builds on the official MCP C# SDK (`ModelContextProtocol.AspNetCore`) and plugs into the same `LinkConfig<T>` declarations the rest of Cairn uses — nothing about an action is declared twice.

The idea is the HATEOAS proposition restated for agents: a hypermedia response tells a *client* what it can do right now; an MCP tool list tells an *agent* the same thing. Cairn already knows the answer — an affordance's `When` predicate encodes the state rule and `RequireAuthorization` the permission — so the MCP surface is derived, not hand-written.

```bash
dotnet add package Cairn.Mcp
```

## Wiring

`WithCairnAffordances` extends the MCP SDK's server builder. You opt resource types in one at a time and tell Cairn how to load an instance from a tool call's `id` argument:

```csharp
builder.Services.AddCairn(o => o.AddLinks(new OrderLinks()));

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithCairnAffordances(mcp =>
    {
        mcp.AddResource<OrderDto>("order", async (id, services, ct) =>
            int.TryParse(id, out var orderId)
                ? await services.GetRequiredService<OrderService>().FindAsync(orderId, ct)
                : null);

        // A singleton resource (a collection page, the API root) needs no id.
        mcp.AddResource<OrdersResource>("orders", async (services, ct) =>
            await services.GetRequiredService<OrderService>().SummaryAsync(ct));
    });

var app = builder.Build();
app.MapMcp("/mcp");
```

Requirements: the ASP.NET Core HTTP transport (`WithHttpTransport` + `MapMcp` — the tools need the caller's identity and the request's base address), a `AddCairn` registration, and a `LinkConfig<T>` for every resource passed to `AddResource`. The MCP endpoint itself is authenticated however you choose — `app.MapMcp("/mcp").RequireAuthorization()` is the usual shape; Cairn's gates then see that user.

## What gets exposed

For a configuration like:

```csharp
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(o => LinkTarget.Route(Routes.GetOrder, new { id = o.Id }));
        builder.Affordance("cancel", o => LinkTarget.Route(Routes.CancelOrder, new { id = o.Id }))
            .Post()
            .Accepts<CancelOrderRequest>()
            .Title("Cancel the order")
            .When(o => o.Status is OrderStatus.Pending);
        builder.Affordance("approve", o => LinkTarget.Route(Routes.ApproveOrder, new { id = o.Id }))
            .Post()
            .RequireAuthorization("manager");
    }
}
```

the MCP server offers:

- **`order_cancel`**, **`order_approve`** — one tool per declared affordance, named `{resource}_{affordance}` (characters MCP tool names disallow are replaced with `-`, so `acme:archive` becomes `acme-archive`). The tool's input schema is the `id` argument plus fields derived from the `Accepts<TInput>()` type — the same serializer-contract-driven derivation HAL-FORMS templates use, so wire names, requiredness (`required`, `[Required]`, non-nullable references), enum values, and `[Range]`/`[StringLength]`/`[RegularExpression]` constraints all carry over.
- **`order_get`** — a state-inspection tool per resource that returns the resource's serialized value together with its links and the actions *currently* advertised to this caller. It's how an agent discovers state before acting; turn it off with `IncludeGetTools = false`.

## How gating works

The two gate kinds apply at the two MCP moments:

- **`tools/list` is filtered by caller-only policies.** An affordance gated with `RequireAuthorization("manager")` (or the parameterless default-policy overload) simply isn't listed for a caller who fails the policy — the agent never sees a button it could never press. Gates that need a resource instance — `When` predicates and resource-based `RequireAuthorization(policy, o => ...)` — can't be decided at list time, so those tools stay listed and their descriptions say the action is state-gated.
- **Every call re-runs all gates.** The tool loads the resource through your loader, rebuilds its link set with the caller's identity — the exact computation that decides what a hypermedia response would advertise — and only proceeds if the affordance is still present. A gated-off action returns a tool error naming the actions that *are* currently available, so the agent can re-plan instead of flailing:

  > The 'cancel' action is not currently available on this order — its state or authorization gates exclude it for this caller. Currently available actions: 'approve'.

## How invocation works

When the gates pass, the default invoker executes the affordance the way a hypermedia client would: an HTTP request to the affordance's own resolved URL, using its declared method, with tool arguments as a JSON body (or query parameters for GET/HEAD) and the MCP request's `Authorization` header forwarded so the endpoint authenticates the same caller. Your endpoint's model binding, validation, and authorization stay authoritative — the MCP layer never bypasses them, it only decides whether to advertise and attempt the action. Non-2xx responses come back as tool errors carrying the status and body.

Knobs on `CairnMcpOptions`:

- `ForwardAuthorizationHeader` (default `true`) — turn off if the self-call should be anonymous or you attach credentials yourself.
- `ConfigureInvocationRequest` — mutate the outgoing `HttpRequestMessage` (forward cookies, add headers) with access to the MCP request's `HttpContext`.
- The invoker sends through an `HttpClient` named `"Cairn.Mcp"`; configure that named client to customize the transport (tests point it at `TestServer.CreateHandler()`).
- Replace `ICairnMcpAffordanceInvoker` entirely to invoke in-process or over another transport — the default also rejects affordances whose `ContentType` isn't JSON (e.g. `multipart/form-data`), which a custom invoker can support.

## Notes and limits

- The tool list is per-caller but clients may cache it; the call-time gate is the enforcement point, the list filter is a courtesy. Both always agree with what your API's hypermedia says.
- Affordances declared on resources you never `AddResource` are simply not exposed — the MCP surface is opt-in per resource, like everything else in Cairn.
- Tool names must be unique: registering the same resource name twice, or declaring an affordance literally named `get` while `IncludeGetTools` is on, fails fast at server start with an explanation.
- The self-invocation requires the app to be reachable from itself (it targets the same host the MCP request arrived on, honoring `CairnOptions.UrlStyle` and `PublicBaseUri`). In restrictive network topologies, swap the invoker.

See [Affordances & HAL-FORMS](affordances-and-forms.md) for the declaration surface the tools are derived from, and [Packages](packages.md) for how `Cairn.Mcp` relates to the rest of the family.
