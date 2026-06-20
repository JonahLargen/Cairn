using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emit stage: a System.Text.Json contract modifier that injects <c>_links</c> and <c>_actions</c>
/// for an instance, reading the hypermedia computed by the endpoint filter — without the DTO
/// declaring either property.
/// </summary>
internal sealed class CairnLinkInjectionModifier(IHttpContextAccessor accessor)
{
    public void Modify(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        AddProperty(typeInfo, "_links", payload => payload.Links);
        AddProperty(typeInfo, "_actions", payload => payload.Actions);
    }

    private void AddProperty(JsonTypeInfo typeInfo, string name, Func<ResourceHypermedia, object?> selector)
    {
        var property = typeInfo.CreateJsonPropertyInfo(typeof(object), name);

        property.Get = instance =>
        {
            var http = accessor.HttpContext;
            if (http is null)
            {
                return null;
            }

            var payload = CairnLinkStore.Lookup(http, instance);
            return payload is null ? null : selector(payload);
        };

        property.ShouldSerialize = static (_, value) => value is not null;
        typeInfo.Properties.Add(property);
    }
}
