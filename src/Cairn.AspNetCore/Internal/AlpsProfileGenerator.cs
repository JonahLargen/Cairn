using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Builds an ALPS profile document (draft-amundsen-richardson-foster-alps) for a resource type from its
/// registered link configuration: the type's serialized properties become <c>semantic</c> descriptors, its
/// declared links become <c>safe</c> descriptors, and its declared affordances become
/// <c>safe</c>/<c>idempotent</c>/<c>unsafe</c> descriptors keyed by HTTP method, with the input type's
/// fields nested as semantic descriptors. Conditional declarations (<c>When</c>/<c>RequireAuthorization</c>)
/// are included: an ALPS profile describes every transition the resource can offer, not one response.
/// </summary>
internal static class AlpsProfileGenerator
{
    public static AlpsDocumentRoot Build(
        Type resourceType,
        ICompiledLinkConfig config,
        JsonSerializerOptions serializer,
        Func<Type, string?> profileHref)
    {
        // Descriptor ids must be unique within the document (they are its fragment identifiers); compare
        // case-insensitively so ids that differ only by case don't coexist confusingly.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Wire name -> descriptor id of every semantic (field) descriptor emitted so far, so an action input
        // that reuses a field references it (href) instead of re-declaring it.
        var semantics = new Dictionary<string, string>(StringComparer.Ordinal);

        var descriptors = new List<AlpsDescriptor>();

        // The resource's own serialized fields, under the wire names the host's serializer emits. Cairn's
        // injected hypermedia placeholders have no member behind them and are skipped.
        foreach (var field in ResourceFieldNames(resourceType, serializer))
        {
            if (used.Add(field))
            {
                semantics[field] = field;
                descriptors.Add(new AlpsDescriptor { Id = field, Type = "semantic" });
            }
        }

        if (config is IDeclarationReportingConfig declared)
        {
            var seen = new HashSet<LinkRelation>();
            foreach (var link in declared.DeclaredLinks)
            {
                if (!seen.Add(link.Relation))
                {
                    continue;   // several declarations of one relation describe one transition
                }

                var id = UniqueId(used, link.Relation.Value, "-link");
                descriptors.Add(new AlpsDescriptor
                {
                    Id = id,
                    Name = id == link.Relation.Value ? null : link.Relation.Value,
                    Type = "safe",
                    Title = link.Title,
                    Doc = link.Deprecation is { } deprecation ? new AlpsDoc($"Deprecated: {deprecation}") : null,
                    Links = link.Profile is { } profile ? [new AlpsLink("profile", profile)] : null,
                });
            }

            seen.Clear();
            foreach (var affordance in declared.DeclaredAffordances)
            {
                if (!seen.Add(affordance.Name))
                {
                    continue;
                }

                var id = UniqueId(used, affordance.Name.Value, "-action");
                descriptors.Add(new AlpsDescriptor
                {
                    Id = id,
                    Name = id == affordance.Name.Value ? null : affordance.Name.Value,
                    Type = TransitionTypeOf(affordance.HttpMethod),
                    Title = affordance.Title,
                    Descriptors = InputDescriptors(affordance.InputType, serializer, used, semantics),
                });
            }
        }

        if (config is IEmbeddedResourceReportingConfig { EmbeddedResources: { Count: > 0 } embeds })
        {
            var seen = new HashSet<LinkRelation>();
            foreach (var embed in embeds)
            {
                if (!seen.Add(embed.Relation))
                {
                    continue;
                }

                var id = UniqueId(used, embed.Relation.Value, "-embedded");
                var href = profileHref(embed.ResourceType);
                descriptors.Add(new AlpsDescriptor
                {
                    Id = id,
                    Name = id == embed.Relation.Value ? null : embed.Relation.Value,
                    Type = "semantic",
                    Doc = new AlpsDoc(embed.Single
                        ? $"Embedded {embed.ResourceType.Name} resource."
                        : $"Embedded collection of {embed.ResourceType.Name} resources."),
                    Links = href is null ? null : [new AlpsLink("profile", href)],
                });
            }
        }

        return new AlpsDocumentRoot(new AlpsDocument
        {
            Doc = new AlpsDoc($"ALPS profile of {resourceType.Name}, generated from its registered Cairn link configuration."),
            Descriptors = descriptors,
        });
    }

    // ALPS classifies transitions by their protocol semantics: safe (GET), idempotent (PUT/DELETE), and
    // unsafe otherwise (POST, PATCH, custom methods). Links are always safe — they are navigations.
    private static string TransitionTypeOf(string httpMethod)
    {
        if (string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return "safe";
        }

        return string.Equals(httpMethod, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(httpMethod, "DELETE", StringComparison.OrdinalIgnoreCase)
            ? "idempotent"
            : "unsafe";
    }

    // An action's input fields, as nested semantic descriptors. A field the document already declares (a
    // resource field, or an earlier action's input) is referenced by fragment instead of re-declared, per
    // ALPS's document-unique ids.
    private static IReadOnlyList<AlpsDescriptor>? InputDescriptors(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type? input,
        JsonSerializerOptions serializer,
        HashSet<string> used,
        Dictionary<string, string> semantics)
    {
        if (input is null)
        {
            return null;
        }

        List<AlpsDescriptor>? nested = null;
        foreach (var field in InputFieldNames(input, serializer))
        {
            AlpsDescriptor descriptor;
            if (semantics.TryGetValue(field, out var id))
            {
                descriptor = new AlpsDescriptor { Href = "#" + id };
            }
            else
            {
                id = UniqueId(used, field, "-input");
                semantics[field] = id;
                descriptor = new AlpsDescriptor { Id = id, Name = id == field ? null : field, Type = "semantic" };
            }

            (nested ??= []).Add(descriptor);
        }

        return nested;
    }

    // The resource type's serialized property names come from the serializer contract alone — the registry
    // hands us a bare Type, so reflection fallbacks (which the trimmer would need annotations for) are out.
    // Under a resolver that cannot produce an object contract for the type, the profile simply carries no
    // field descriptors.
    private static IEnumerable<string> ResourceFieldNames(Type resourceType, JsonSerializerOptions serializer)
    {
        if (ContractOf(resourceType, serializer) is not { Kind: JsonTypeInfoKind.Object } contract)
        {
            yield break;
        }

        foreach (var property in contract.Properties)
        {
            // Only a property backed by a real member is the DTO's own; Cairn's injected _links/_actions/...
            // placeholders have no AttributeProvider.
            if (property.AttributeProvider is PropertyInfo member && member.GetIndexParameters().Length == 0)
            {
                yield return property.Name;
            }
        }
    }

    // Input types are annotated (Accepts<TInput> preserves public properties), so when the serializer has no
    // contract for one — e.g. a source-gen-only resolver that doesn't know it — reflection plus the options'
    // naming policy still yields the wire names, mirroring HalFormsSchema.ContractProperties.
    private static IEnumerable<string> InputFieldNames(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type input,
        JsonSerializerOptions serializer)
    {
        if (ContractOf(input, serializer) is { Kind: JsonTypeInfoKind.Object } contract)
        {
            foreach (var property in contract.Properties)
            {
                if (property.AttributeProvider is PropertyInfo member && member.GetIndexParameters().Length == 0)
                {
                    yield return property.Name;
                }
            }

            yield break;
        }

        foreach (var property in input.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                yield return property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? serializer.PropertyNamingPolicy?.ConvertName(property.Name)
                    ?? property.Name;
            }
        }
    }

    private static JsonTypeInfo? ContractOf(Type type, JsonSerializerOptions serializer)
    {
        try
        {
            return serializer.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // Claims a document-unique descriptor id: the preferred id itself, then suffixed by kind
    // ("cancel-action"), then numbered ("cancel-action-2", ...). The caller emits the original value as the
    // descriptor's name whenever the id had to move.
    private static string UniqueId(HashSet<string> used, string preferred, string suffix)
    {
        if (used.Add(preferred))
        {
            return preferred;
        }

        var candidate = preferred + suffix;
        if (used.Add(candidate))
        {
            return candidate;
        }

        for (var n = 2; ; n++)
        {
            var numbered = $"{candidate}-{n}";
            if (used.Add(numbered))
            {
                return numbered;
            }
        }
    }
}
