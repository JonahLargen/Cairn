using System.Collections.Concurrent;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Once-only gate for diagnostics: the first caller to mark a (category, key) pair wins, so a
/// misconfiguration is logged once instead of on every request. Registered as a singleton by
/// <c>AddCairn</c>, so the gate is per host container — a second host in the same process (test
/// suites, side-by-side hosts) gets its own diagnostics rather than losing them to another host's marks.
/// </summary>
internal sealed class WarnOnce
{
    private readonly ConcurrentDictionary<string, bool> _marked = new(StringComparer.Ordinal);

    /// <summary>Whether this is the first time the (category, key) pair is seen by this host.</summary>
    public bool Mark(string category, string key) => _marked.TryAdd($"{category}|{key}", true);

    /// <summary>Whether this is the first time the (category, type) pair is seen by this host.</summary>
    public bool Mark(string category, Type type) => Mark(category, type.FullName ?? type.Name);
}
