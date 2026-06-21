using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emit stage: a System.Text.Json contract modifier that injects hypermedia for an instance in the
/// request's negotiated format — <c>_links</c> always, <c>_actions</c> (Default) or <c>_templates</c>
/// (HAL-FORMS) for affordances — without the DTO declaring any of them.
/// </summary>
internal sealed class CairnLinkInjectionModifier(IHttpContextAccessor accessor)
{
    public void Modify(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        AddProperty(typeInfo, "_links", static (payload, _) => payload.Links);
        AddProperty(typeInfo, "_embedded", static (payload, _) => payload.Embedded);
        AddProperty(typeInfo, "_actions", static (payload, format) => format == HypermediaFormat.Default ? payload.Actions : null);
        AddProperty(typeInfo, "_templates", static (payload, format) => format == HypermediaFormat.HalForms ? ToTemplates(payload.Actions) : null);
    }

    private void AddProperty(JsonTypeInfo typeInfo, string name, Func<ResourceHypermedia, HypermediaFormat, object?> selector)
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
            var http = accessor.HttpContext;
            if (http is null)
            {
                return null;
            }

            var payload = CairnLinkStore.Lookup(http, instance);
            return payload is null ? null : selector(payload, CairnLinkStore.GetFormat(http));
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
