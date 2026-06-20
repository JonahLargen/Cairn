using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for registering Cairn services.</summary>
public static class CairnServiceCollectionExtensions
{
    /// <summary>Adds Cairn hypermedia services to the container.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddCairn(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TODO: register the link-building engine, the JsonTypeInfo modifier, and options.
        return services;
    }
}
