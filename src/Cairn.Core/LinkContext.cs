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
    /// <exception cref="ArgumentNullException"><paramref name="urlResolver"/> or <paramref name="authorizer"/> is null.</exception>
    public LinkContext(ILinkUrlResolver urlResolver, ILinkAuthorizer authorizer, LinkResolutionMode mode = LinkResolutionMode.Lax)
    {
        ArgumentNullException.ThrowIfNull(urlResolver);
        ArgumentNullException.ThrowIfNull(authorizer);

        UrlResolver = urlResolver;
        Authorizer = authorizer;
        Mode = mode;
    }

    /// <summary>Resolves link targets to URLs.</summary>
    public ILinkUrlResolver UrlResolver { get; }

    /// <summary>Authorizes policy-gated links.</summary>
    public ILinkAuthorizer Authorizer { get; }

    /// <summary>How unresolved targets are handled.</summary>
    public LinkResolutionMode Mode { get; }
}
