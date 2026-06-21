using System.Text.Json.Serialization.Metadata;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for registering Cairn services.</summary>
public static class CairnServiceCollectionExtensions
{
    /// <summary>Adds Cairn hypermedia services and the response link projection.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration (link registrations and resolution mode).</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddCairn(this IServiceCollection services, Action<CairnOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CairnOptions();
        configure?.Invoke(options);

        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton<ILinkConfigProvider>(options.Registry);
        services.AddSingleton<ILinkEngine, LinkEngine>();
        services.AddScoped<ILinkUrlResolver, LinkGeneratorUrlResolver>();
        services.AddScoped<ILinkAuthorizer, AuthorizationPolicyLinkAuthorizer>();

        var modifier = new CairnLinkInjectionModifier(new HttpContextAccessor());

        // Minimal APIs (and WriteAsJsonAsync) serialize through Http.Json options.
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolver =
                (json.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                    .WithAddedModifier(modifier.Modify);
        });

        // MVC controllers serialize through the System.Text.Json output formatter, which reads Mvc.JsonOptions.
        // Configuring it is harmless when MVC is not in use.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(mvc =>
        {
            mvc.JsonSerializerOptions.TypeInfoResolver =
                (mvc.JsonSerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                    .WithAddedModifier(modifier.Modify);
        });

        return services;
    }
}
