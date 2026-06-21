namespace Cairn;

/// <summary>How the engine handles a link target that cannot be resolved to a URL.</summary>
public enum LinkResolutionMode
{
    /// <summary>Silently omit links that fail to resolve.</summary>
    Lax,

    /// <summary>Throw when a link fails to resolve.</summary>
    Strict,
}

/// <summary>Resolves a <see cref="LinkTarget"/> to a URL.</summary>
public interface ILinkUrlResolver
{
    /// <summary>Resolves the target, or returns <see langword="null"/> if it cannot be resolved.</summary>
    string? Resolve(LinkTarget target);
}

/// <summary>Evaluates whether the current caller satisfies an authorization policy.</summary>
public interface ILinkAuthorizer
{
    /// <summary>Returns whether the caller satisfies the named policy.</summary>
    ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default);
}

/// <summary>Per-request inputs the engine needs to resolve and authorize links.</summary>
public sealed class LinkContext
{
    /// <summary>Creates a link context.</summary>
    /// <param name="urlResolver">Resolves link targets to URLs.</param>
    /// <param name="authorizer">Authorizes policy-gated links.</param>
    /// <param name="mode">How unresolved targets are handled.</param>
    /// <param name="services">The request's services, for service-aware conditions and targets.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="urlResolver"/> or <paramref name="authorizer"/> is null.</exception>
    public LinkContext(
        ILinkUrlResolver urlResolver,
        ILinkAuthorizer authorizer,
        LinkResolutionMode mode = LinkResolutionMode.Lax,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urlResolver);
        ArgumentNullException.ThrowIfNull(authorizer);

        UrlResolver = urlResolver;
        Authorizer = authorizer;
        Mode = mode;
        Services = services ?? EmptyServiceProvider.Instance;
        CancellationToken = cancellationToken;
    }

    /// <summary>Resolves link targets to URLs.</summary>
    public ILinkUrlResolver UrlResolver { get; }

    /// <summary>Authorizes policy-gated links.</summary>
    public ILinkAuthorizer Authorizer { get; }

    /// <summary>How unresolved targets are handled.</summary>
    public LinkResolutionMode Mode { get; }

    /// <summary>The request's service provider, for service-aware conditions and targets (e.g. fetching data not on the DTO).</summary>
    public IServiceProvider Services { get; }

    /// <summary>The request's cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
