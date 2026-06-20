namespace Cairn.AspNetCore;

/// <summary>Configures Cairn's hypermedia services.</summary>
public sealed class CairnOptions
{
    internal LinkConfigRegistry Registry { get; } = new();

    /// <summary>How unresolved link targets are handled (default <see cref="LinkResolutionMode.Lax"/>).</summary>
    public LinkResolutionMode Mode { get; set; } = LinkResolutionMode.Lax;

    /// <summary>Registers the link configuration for resources of type <typeparamref name="T"/>.</summary>
    public CairnOptions AddLinks<T>(LinkConfig<T> config)
    {
        Registry.Add(config);
        return this;
    }
}
