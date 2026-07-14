using Cairn.Mcp.Internal;
using Microsoft.AspNetCore.Http;

namespace Cairn.Mcp;

/// <summary>Configures which resources the MCP server exposes and how their affordances are invoked.</summary>
public sealed class CairnMcpOptions
{
    internal List<CairnMcpResourceRegistration> Resources { get; } = [];

    /// <summary>
    /// Whether each registered resource also gets a <c>{name}_get</c> tool that returns the resource's current
    /// state together with its links and the actions currently available to the caller. Defaults to
    /// <see langword="true"/>; the tool is how an agent discovers state before invoking an action.
    /// </summary>
    public bool IncludeGetTools { get; set; } = true;

    /// <summary>
    /// Whether the default invoker copies the MCP request's <c>Authorization</c> header onto the HTTP request
    /// that executes an affordance, so the target endpoint authenticates the same caller. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool ForwardAuthorizationHeader { get; set; } = true;

    /// <summary>
    /// Customizes the HTTP request the default invoker sends for an affordance — add headers, forward cookies,
    /// or attach credentials the <c>Authorization</c> header alone cannot carry. Receives the MCP request's
    /// <see cref="HttpContext"/> and the outgoing request after the default headers are applied.
    /// </summary>
    public Action<HttpContext, HttpRequestMessage>? ConfigureInvocationRequest { get; set; }

    /// <summary>
    /// Exposes the affordances declared for <typeparamref name="T"/> as MCP tools named
    /// <c>{name}_{affordance}</c>, each taking an <c>id</c> plus the affordance's declared input fields.
    /// </summary>
    /// <typeparam name="T">The resource type; must have a link configuration registered with Cairn.</typeparam>
    /// <param name="name">
    /// The resource's tool-name prefix (letters, digits, <c>_</c> or <c>-</c>), e.g. <c>"order"</c> yields
    /// <c>order_cancel</c>.
    /// </param>
    /// <param name="loader">
    /// Loads the resource instance a tool call targets from the caller-supplied <c>id</c>, or returns
    /// <see langword="null"/> when no such resource exists. Runs with the MCP request's scoped services.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or contains characters other than letters, digits, <c>_</c> or <c>-</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="loader"/> is null.</exception>
    public CairnMcpOptions AddResource<T>(string name, Func<string, IServiceProvider, CancellationToken, ValueTask<T?>> loader) where T : class
    {
        ArgumentNullException.ThrowIfNull(loader);
        Resources.Add(new CairnMcpResourceRegistration<T>(ValidName(name), (id, services, cancellationToken) => loader(id!, services, cancellationToken)) { RequiresId = true });
        return this;
    }

    /// <summary>
    /// Exposes the affordances declared for <typeparamref name="T"/> as MCP tools named
    /// <c>{name}_{affordance}</c> for a resource that needs no identifier — a singleton such as a collection
    /// resource or an API root.
    /// </summary>
    /// <typeparam name="T">The resource type; must have a link configuration registered with Cairn.</typeparam>
    /// <param name="name">The resource's tool-name prefix (letters, digits, <c>_</c> or <c>-</c>).</param>
    /// <param name="loader">
    /// Loads the resource instance a tool call targets, or returns <see langword="null"/> when it is
    /// unavailable. Runs with the MCP request's scoped services.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or contains characters other than letters, digits, <c>_</c> or <c>-</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="loader"/> is null.</exception>
    public CairnMcpOptions AddResource<T>(string name, Func<IServiceProvider, CancellationToken, ValueTask<T?>> loader) where T : class
    {
        ArgumentNullException.ThrowIfNull(loader);
        Resources.Add(new CairnMcpResourceRegistration<T>(ValidName(name), (_, services, cancellationToken) => loader(services, cancellationToken)) { RequiresId = false });
        return this;
    }

    private static string ValidName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Resource name must not be null or empty.", nameof(name));
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                throw new ArgumentException($"Resource name '{name}' must contain only letters, digits, '_' or '-' so it can prefix MCP tool names.", nameof(name));
            }
        }

        return name;
    }
}
