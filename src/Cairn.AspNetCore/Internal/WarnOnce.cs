using System.Collections.Concurrent;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Process-wide once-only gate for diagnostics: the first caller to mark a (category, key) pair wins, so a
/// misconfiguration is logged once instead of on every request. Replaces the per-diagnostic static
/// dictionaries that used to accumulate across the codebase.
/// </summary>
internal static class WarnOnce
{
    private static readonly ConcurrentDictionary<string, bool> Marked = new(StringComparer.Ordinal);

    /// <summary>Whether this is the first time the (category, key) pair is seen in the process.</summary>
    public static bool Mark(string category, string key) => Marked.TryAdd($"{category}|{key}", true);

    /// <summary>Whether this is the first time the (category, type) pair is seen in the process.</summary>
    public static bool Mark(string category, Type type) => Mark(category, type.FullName ?? type.Name);
}
