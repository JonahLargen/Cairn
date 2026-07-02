using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for registering Cairn services.</summary>
public static class CairnServiceCollectionExtensions
{
    /// <summary>Adds Cairn hypermedia services and the response link projection.</summary>
    /// <remarks>
    /// Options follow the standard options pattern: calling <c>AddCairn</c> several times composes every
    /// <paramref name="configure"/> delegate, and configuration that needs other services can be added with
    /// <c>services.AddOptions&lt;CairnOptions&gt;().Configure&lt;TDep&gt;(...)</c>. The JSON wiring is applied
    /// as a post-configuration step, so a <c>TypeInfoResolver</c> the app assigns later (e.g. a source-generated
    /// <c>JsonSerializerContext</c>) is wrapped rather than silently replacing Cairn's link injection.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration (link registrations and resolution mode).</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddCairn(this IServiceCollection services, Action<CairnOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpContextAccessor();
        services.TryAddSingleton<WarnOnce>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, CairnHeadersStartupFilter>());
        services.TryAddSingleton<CairnOptions>(static provider => provider.GetRequiredService<IOptions<CairnOptions>>().Value);
        services.TryAddSingleton<ILinkConfigProvider>(static provider => provider.GetRequiredService<CairnOptions>().Registry);
        services.TryAddSingleton<ILinkEngine, LinkEngine>();
        services.TryAddScoped<ILinkUrlResolver, LinkGeneratorUrlResolver>();
        services.TryAddScoped<ILinkAuthorizer, AuthorizationPolicyLinkAuthorizer>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>, CairnJsonOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>, CairnJsonOptionsSetup>());

        return services;
    }
}
