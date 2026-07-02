using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emit stage: a System.Text.Json contract modifier that injects hypermedia for an instance in the
/// request's negotiated format — <c>_links</c> always, <c>_actions</c> (Default) or <c>_templates</c>
/// (HAL-FORMS) for affordances — without the DTO declaring any of them. When a custom
/// <see cref="IHypermediaFormatter"/> is active for the request, its properties are injected instead.
/// </summary>
internal sealed class CairnLinkInjectionModifier
{
    private static readonly string[] BuiltInNames = ["_links", "_embedded", "_actions", "_templates"];

    private readonly IHttpContextAccessor _accessor;
    private readonly Dictionary<IHypermediaFormatter, Dictionary<string, Func<HypermediaDocument, object?>>> _custom = [];
    private readonly List<string> _names = [.. BuiltInNames];

    public CairnLinkInjectionModifier(IHttpContextAccessor accessor, CairnOptions options)
    {
        _accessor = accessor;

        // The contract is built once per type and cached, so every property any registered formatter can emit
        // must be declared up front; each property's getter then projects only for the request's active format.
        foreach (var formatter in options.Formatters)
        {
            var properties = new Dictionary<string, Func<HypermediaDocument, object?>>(StringComparer.Ordinal);
            foreach (var property in formatter.Properties)
            {
                properties[property.Name] = property.Value;
                if (!_names.Contains(property.Name, StringComparer.Ordinal))
                {
                    _names.Add(property.Name);
                }
            }

            _custom[formatter] = properties;
        }
    }

    public void Modify(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var name in _names)
        {
            AddProperty(typeInfo, name);
        }
    }

    private void AddProperty(JsonTypeInfo typeInfo, string name)
    {
        // Don't collide with a DTO that already declares a property of this JSON name — System.Text.Json
        // rejects duplicate property names when the contract is finalized, which would fail serialization.
        foreach (var existing in typeInfo.Properties)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                return;
            }
        }

        var property = typeInfo.CreateJsonPropertyInfo(typeof(object), name);

        property.Get = instance =>
        {
            var http = _accessor.HttpContext;
            if (http is null)
            {
                return null;
            }

            var payload = CairnLinkStore.Lookup(http, instance);
            if (payload is null)
            {
                return null;
            }

            // An active custom formatter supersedes the built-in emission entirely.
            if (CairnLinkStore.GetFormatter(http) is { } custom)
            {
                return _custom.TryGetValue(custom, out var properties) && properties.TryGetValue(name, out var project)
                    ? project(payload.ToDocument())
                    : null;
            }

            return name switch
            {
                "_links" => payload.Links,
                "_embedded" => payload.Embedded,
                "_actions" when CairnLinkStore.GetFormat(http) == HypermediaFormat.Default => payload.Actions,
                "_templates" when CairnLinkStore.GetFormat(http) == HypermediaFormat.HalForms => ToTemplates(payload.Actions),
                _ => null,
            };
        };

        property.ShouldSerialize = static (_, value) => value is not null;
        typeInfo.Properties.Add(property);
    }

    private static IReadOnlyDictionary<string, HalFormsTemplate>? ToTemplates(IReadOnlyDictionary<string, HalAction>? actions)
        => actions is null
            ? null
            : actions.ToDictionary(
                a => a.Key,
                a => new HalFormsTemplate(a.Value.Method, a.Value.Href)
                {
                    Title = a.Value.Title,
                    ContentType = a.Value.ContentType ?? "application/json",
                    Properties = HalFormsSchema.For(a.Value.Input),
                });
}
