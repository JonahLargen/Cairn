using Cairn;

namespace Cairn.Benchmarks;

public sealed record OrderDto(int Id, int Status);

public sealed record UnconfiguredDto(int Id);

public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("BenchGetOrder", new { id = order.Id }));
        builder.Link("collection", _ => LinkTarget.Uri("/orders"));
        builder.Affordance("cancel", order => LinkTarget.Route("BenchCancelOrder", new { id = order.Id }))
            .Post()
            .When(order => order.Status == 1);
    }
}
