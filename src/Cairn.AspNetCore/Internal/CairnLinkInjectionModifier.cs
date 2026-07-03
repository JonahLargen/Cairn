using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private readonly CairnOptions _options;
    private readonly ILinkConfigProvider _configs;
    private readonly Dictionary<IHypermediaFormatter, Dictionary<string, Func<HypermediaDocument, object?>>> _custom = [];
    private readonly List<string> _names = [.. BuiltInNames];

    public CairnLinkInjectionModifier(IHttpContextAccessor accessor, CairnOptions options, ILinkConfigProvider configs)
    {
        _accessor = accessor;
        _options = options;
        _configs = configs;

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
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !CanCarryHypermedia(typeInfo.Type))
        {
            return;
        }

        foreach (var name in _names)
        {
            AddProperty(typeInfo, name);
        }
    }

    // Only a type the compute stage can actually record hypermedia for gets the contract properties: one
    // with a registered link config (its own or a base class's), a pagination envelope, or a polymorphic base
    // of a configured type (see HasConfiguredSubtype). Injecting into every object contract would put four
    // phantom properties on every schema a JsonTypeInfo-driven document generator (AddOpenApi) produces —
    // request bodies and DTOs Cairn never touches included.
    private bool CanCarryHypermedia(Type type)
        => _configs.GetConfig(type) is not null
            || typeof(IPagedResource).IsAssignableFrom(type)
            || typeof(ICursorPagedResource).IsAssignableFrom(type)
            || _options.IsPagingEnvelope(type)
            || HasConfiguredSubtype(type);

    // A non-sealed type is a polymorphic base: an instance of a configured subtype can be serialized through
    // its declared-type contract, so that contract must carry the injected properties even though the base
    // itself has no config. Without this, a List<Animal> of configured Dog items serializes through the Animal
    // contract and emits no links — the compute stage records against the runtime Dog, keyed by the instance,
    // but the declared-type contract the serializer actually uses never asked for the properties. The
    // OpenAPI/Swagger schema transformers strip the resulting placeholders from the base type's own schema
    // (which documents no hypermedia of its own). A custom ILinkConfigProvider that is not the built-in
    // registry cannot be reverse-queried, so this path is simply skipped for it.
    private bool HasConfiguredSubtype(Type type)
        => type is { IsClass: true, IsSealed: false }
            && _configs is LinkConfigRegistry registry
            && registry.HasConfiguredSubtype(type);

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
                "_templates" when CairnLinkStore.GetFormat(http) == HypermediaFormat.HalForms => ToTemplates(payload.Actions, typeInfo.Options, http, typeInfo.Type),
                _ => null,
            };
        };

        property.ShouldSerialize = static (_, value) => value is not null;
        typeInfo.Properties.Add(property);
    }

    private static IReadOnlyDictionary<string, HalFormsTemplate>? ToTemplates(IReadOnlyDictionary<string, HalAction>? actions, System.Text.Json.JsonSerializerOptions serializer, HttpContext http, Type resourceType)
    {
        if (actions is null)
        {
            return null;
        }

        // Template names serialize verbatim (never renamed by DictionaryKeyPolicy); an affordance marked
        // AsDefault() emits under the reserved "default" key HAL-FORMS clients look up first. A resource
        // whose response carries exactly one template is keyed "default" too — it is unambiguously the
        // primary action, and a generic HAL-FORMS client that only knows the reserved key should find it
        // without the config author remembering AsDefault().
        var sole = actions.Count == 1;
        var templates = new VerbatimKeyDictionary<HalFormsTemplate>(StringComparer.OrdinalIgnoreCase);
        string? defaultClaimant = null;
        foreach (var (name, action) in actions)
        {
            var key = sole || action.IsDefault ? "default" : name;
            if (string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
            {
                // Registration-time validation only rejects *unconditional* double claims; When()- or
                // policy-gated AsDefault() affordances are meant to be mutually exclusive at runtime. When
                // that invariant breaks and two emit on one response, the wire silently keeps the last —
                // surface it.
                if (defaultClaimant is not null)
                {
                    WarnDefaultCollision(http, resourceType, defaultClaimant, name);
                }

                defaultClaimant = name;
            }

            templates[key] = new HalFormsTemplate(action.Method, action.Href)
            {
                Title = action.Title,
                ContentType = action.ContentType ?? "application/json",
                Properties = HalFormsSchema.For(action.Input, serializer),
            };
        }

        return templates;
    }

    private static void WarnDefaultCollision(HttpContext http, Type resourceType, string first, string second)
    {
        var services = http.RequestServices;
        if (services.GetService<ILoggerFactory>() is { } loggerFactory
            && services.GetService<WarnOnce>() is { } warnOnce
            && warnOnce.Mark("default-template-collision", resourceType))
        {
            loggerFactory.CreateLogger("Cairn.AspNetCore").LogWarning(
                "Cairn: affordances '{First}' and '{Second}' on {ResourceType} both claimed the reserved 'default' HAL-FORMS template key on the same response; only the last one is emitted. Make the When()/RequireAuthorization() conditions of AsDefault() affordances mutually exclusive.",
                first,
                second,
                resourceType.Name);
        }
    }
}
