namespace Cairn.Core.Tests;

public class LinkConfigRegistryRaceTests
{
    [Fact]
    public void A_negative_cached_before_an_add_is_invalidated_by_the_add()
    {
        var registry = new LinkConfigRegistry();

        Assert.Null(registry.GetConfig(typeof(RaceResource)));

        registry.Add(new RaceLinks());

        Assert.NotNull(registry.GetConfig(typeof(RaceResource)));
    }

    [Fact]
    public async Task A_lookup_racing_a_runtime_add_never_pins_a_stale_negative()
    {
        // Regression: GetConfig could resolve "no config", lose the race to Add's cache invalidation, and
        // then store the stale negative into the (already-cleared) cache — where it lived forever. Hammer
        // the interleaving; with the generation-swapped cache the post-Add lookup below must always hit.
        for (var i = 0; i < 2_000; i++)
        {
            var registry = new LinkConfigRegistry();
            using var raceStart = new ManualResetEventSlim();

            var lookup = Task.Run(() =>
            {
                raceStart.Wait();
                registry.GetConfig(typeof(RaceResource));
            });

            raceStart.Set();
            registry.Add(new RaceLinks());
            await lookup;

            Assert.NotNull(registry.GetConfig(typeof(RaceResource)));
        }
    }

    private sealed record RaceResource(int Id);

    private sealed class RaceLinks : LinkConfig<RaceResource>
    {
        public override void Configure(ILinkBuilder<RaceResource> builder)
            => builder.Self(r => LinkTarget.Uri($"/race/{r.Id}"));
    }
}
