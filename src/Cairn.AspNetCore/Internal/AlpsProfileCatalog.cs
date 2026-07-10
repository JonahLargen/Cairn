using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// The set of ALPS profiles derived from the registered link configs: names each resource type, builds and
/// caches the serialized documents. The registry is frozen by the time this is constructed (options freeze on
/// first resolution), so profiles are computed once per request path base — the only per-request input, since
/// cross-profile links embed it.
/// </summary>
internal sealed class AlpsProfileCatalog
{
    private readonly string _path;
    private readonly JsonSerializerOptions _serializer;
    private readonly IReadOnlyList<Entry> _entries;
    private readonly Dictionary<Type, Entry> _byType = [];

    // Path bases come from server configuration (UsePathBase, hosting), not from the client, so this cannot
    // be grown by request forgery; almost every app has exactly one entry.
    private readonly ConcurrentDictionary<string, Payloads> _payloads = new(StringComparer.Ordinal);

    internal sealed record Entry(string Name, Type ResourceType, ICompiledLinkConfig Config);

    private sealed record Payloads(byte[] Index, IReadOnlyDictionary<string, byte[]> Documents);

    public AlpsProfileCatalog(LinkConfigRegistry registry, JsonSerializerOptions serializer, string path, Func<Type, string>? profileName)
    {
        _path = path;
        _serializer = serializer;

        // Registration is dictionary-ordered; sort by full type name so names, dedup suffixes, and the index
        // are deterministic across runs.
        var naming = profileName ?? DefaultProfileName;
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, config) in registry.Snapshot.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            var name = naming(type);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Cairn: the ALPS ProfileName callback returned an empty name for {type}. Every registered resource type needs a non-empty profile name.");
            }

            // Two types that map to one name (same type name in different namespaces, or a coarse custom
            // callback) get deterministic numeric suffixes rather than clobbering each other.
            var unique = name;
            for (var n = 2; !names.Add(unique); n++)
            {
                unique = $"{name}-{n}";
            }

            var entry = new Entry(unique, type, config);
            entries.Add(entry);
            _byType[type] = entry;
        }

        _entries = entries;
    }

    public byte[] IndexFor(string pathBase) => PayloadsFor(pathBase).Index;

    public byte[]? DocumentFor(string pathBase, string profile)
        => PayloadsFor(pathBase).Documents.TryGetValue(profile, out var document) ? document : null;

    private Payloads PayloadsFor(string pathBase) => _payloads.GetOrAdd(pathBase, Build);

    private Payloads Build(string pathBase)
    {
        string HrefOf(Entry entry) => pathBase + _path + "/" + Uri.EscapeDataString(entry.Name);
        string? ProfileHref(Type resourceType) => _byType.TryGetValue(resourceType, out var entry) ? HrefOf(entry) : null;

        var index = new List<AlpsIndexEntry>(_entries.Count);
        var documents = new Dictionary<string, byte[]>(_entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            index.Add(new AlpsIndexEntry(entry.Name, entry.ResourceType.ToString(), HrefOf(entry)));
            var document = AlpsProfileGenerator.Build(entry.ResourceType, entry.Config, _serializer, ProfileHref);
            documents[entry.Name] = JsonSerializer.SerializeToUtf8Bytes(document, AlpsJsonContext.Default.AlpsDocumentRoot);
        }

        return new Payloads(JsonSerializer.SerializeToUtf8Bytes(new AlpsIndex(index), AlpsJsonContext.Default.AlpsIndex), documents);
    }

    // OrderDto -> order-dto; PagedResource<OrderDto> -> paged-resource-order-dto. Kebab-case of the CLR
    // name reads well in a URL and never needs escaping; no suffix stripping ("Dto", "Resource") — a
    // predictable name beats a pretty one.
    internal static string DefaultProfileName(Type type)
    {
        var builder = new StringBuilder();
        Append(builder, type);
        return builder.ToString();

        static void Append(StringBuilder builder, Type type)
        {
            var name = type.Name;
            var arity = name.IndexOf('`');
            if (arity >= 0)
            {
                name = name[..arity];
            }

            Kebab(builder, name);
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    builder.Append('-');
                    Append(builder, argument);
                }
            }
        }

        static void Kebab(StringBuilder builder, string name)
        {
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    // A word starts at lower/digit->upper ("orderDto") and at the last upper of an acronym
                    // run followed by a lower ("HALDocument" -> hal-document).
                    var boundary = i > 0
                        && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                            || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
                    if (boundary)
                    {
                        builder.Append('-');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }
        }
    }
}
