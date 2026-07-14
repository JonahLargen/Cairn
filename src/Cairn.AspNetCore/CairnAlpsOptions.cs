namespace Cairn.AspNetCore;

/// <summary>Configures the ALPS profile endpoints mounted by <c>MapCairnAlps</c>.</summary>
public sealed class CairnAlpsOptions
{
    private string _path = "/alps";

    /// <summary>
    /// The path the profiles are served under (default <c>/alps</c>): the index document at the path itself,
    /// and each profile at <c>{Path}/{profile-name}</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, whitespace, or does not start with <c>/</c>.</exception>
    public string Path
    {
        get => _path;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var trimmed = value.TrimEnd('/');
            if (trimmed.Length == 0 || trimmed[0] != '/')
            {
                throw new ArgumentException("The ALPS path must start with '/' and name at least one segment (e.g. \"/alps\").", nameof(value));
            }

            _path = trimmed;
        }
    }

    /// <summary>
    /// Names the profile of a resource type — the URL segment its document is served under. When unset,
    /// types are named by kebab-casing the CLR type name (<c>OrderDto</c> → <c>order-dto</c>). Two types
    /// that map to the same name are disambiguated with deterministic numeric suffixes.
    /// </summary>
    public Func<Type, string>? ProfileName { get; set; }
}
