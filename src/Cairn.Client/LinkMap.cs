namespace Cairn.Client;

/// <summary>Helpers for projecting grouped (multi-link-per-relation) links to the common one-per-relation view.</summary>
internal static class LinkMap
{
    /// <summary>Flattens grouped links to one per relation (the first), for the common single-link case.</summary>
    public static IReadOnlyDictionary<string, Link> Flatten(IReadOnlyDictionary<string, IReadOnlyList<Link>> grouped)
    {
        // Relation types are case-insensitive (RFC 8288 §2.1).
        var flat = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relation, links) in grouped)
        {
            if (links.Count > 0)
            {
                flat[relation] = links[0];
            }
        }

        return flat;
    }
}
