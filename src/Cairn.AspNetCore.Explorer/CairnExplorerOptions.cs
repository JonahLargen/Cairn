using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Explorer;

/// <summary>
/// Configures the <see cref="CairnExplorerApplicationBuilderExtensions.UseCairnExplorer(Microsoft.AspNetCore.Builder.IApplicationBuilder, System.Action{CairnExplorerOptions}?)">HAL
/// Explorer</see> — the path it is mounted at, the resource it opens on, and whether it is served at all.
/// </summary>
public sealed class CairnExplorerOptions
{
    private PathString _path = "/explorer";
    private string _entryPoint = "/";
    private string _title = "Cairn HAL Explorer";

    /// <summary>
    /// The path the explorer UI is served from. Default <c>/explorer</c>. Must be a non-empty absolute path;
    /// the explorer answers both the exact path and its trailing-slash form. (<see cref="PathString"/> already
    /// rejects a value that does not start with <c>/</c>.)
    /// </summary>
    /// <exception cref="System.ArgumentException">The value is empty.</exception>
    public PathString Path
    {
        get => _path;
        set
        {
            if (!value.HasValue)
            {
                throw new System.ArgumentException(
                    "The explorer path must be a non-empty absolute path, e.g. \"/explorer\".",
                    nameof(Path));
            }

            _path = value;
        }
    }

    /// <summary>
    /// The resource URL the explorer loads first — the API's entry point. Default <c>/</c>. This is the
    /// address the UI issues its opening <c>GET</c> against; from there the user navigates by following links.
    /// </summary>
    /// <exception cref="System.ArgumentException">The value is null or whitespace.</exception>
    public string EntryPoint
    {
        get => _entryPoint;
        set
        {
            System.ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _entryPoint = value;
        }
    }

    /// <summary>The title shown in the explorer's masthead and browser tab. Default <c>Cairn HAL Explorer</c>.</summary>
    /// <exception cref="System.ArgumentException">The value is null or whitespace.</exception>
    public string Title
    {
        get => _title;
        set
        {
            System.ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _title = value;
        }
    }

    /// <summary>
    /// Whether the explorer is served. When left <see langword="null"/> (the default) it is served only in the
    /// <c>Development</c> environment — the explorer exposes the whole API surface interactively, so it is
    /// off outside development unless you opt in. Set <see langword="true"/> to serve it everywhere (guard it
    /// behind authentication if you do) or <see langword="false"/> to disable it unconditionally.
    /// </summary>
    public bool? Enabled { get; set; }
}
