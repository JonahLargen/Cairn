namespace Cairn.Internal;

/// <summary>A compiled config that can report the authorization policy names its declarations reference.</summary>
internal interface IPolicyReportingConfig
{
    /// <summary>The distinct policy names referenced by links and affordances (the default-policy sentinel excluded).</summary>
    IReadOnlyCollection<string> Policies { get; }
}

/// <summary>
/// A <see cref="LinkConfig{T}"/> compiled once into its specs, able to build a <see cref="LinkSet"/>
/// for an instance supplied as <see cref="object"/> (enabling runtime-type dispatch).
/// </summary>
internal sealed class CompiledLinkConfig<T> : ICompiledLinkConfig, IPolicyReportingConfig, IEmbeddedResourceReportingConfig
{
    private readonly LinkBuilder<T> _builder;
    private IReadOnlyList<EmbeddedResourceSchema>? _embeddedResources;

    private CompiledLinkConfig(LinkBuilder<T> builder) => _builder = builder;

    public static CompiledLinkConfig<T> Compile(LinkConfig<T> config)
    {
        var builder = new LinkBuilder<T>();
        config.Configure(builder);
        ThrowOnDefaultTemplateCollision(builder);
        return new CompiledLinkConfig<T>(builder);
    }

    // An affordance marked AsDefault() emits under the reserved "default" HAL-FORMS template key — as does one
    // literally named "default" (template keys compare case-insensitively). Two claimants that always emit
    // would collide last-wins on the wire with no trace, so fail deterministically at registration time. A
    // claimant gated by When()/RequireAuthorization() may not emit, and several gated claimants with mutually
    // exclusive conditions are a legitimate pattern ("approve is the default when pending, reopen when
    // closed") — so only unconditional claimants count toward the collision.
    private static void ThrowOnDefaultTemplateCollision(LinkBuilder<T> builder)
    {
        AffordanceSpec<T>? claimant = null;
        foreach (var spec in builder.AffordanceSpecs)
        {
            if (!spec.IsDefault && !string.Equals(spec.Relation.Value, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (spec.Condition is not null || spec.Policy is not null)
            {
                continue;
            }

            if (claimant is not null)
            {
                throw new InvalidOperationException(
                    $"The link configuration for {typeof(T).Name} declares more than one affordance unconditionally claiming the reserved 'default' HAL-FORMS template key: " +
                    $"'{claimant.Relation.Value}'{(claimant.IsDefault ? " (AsDefault)" : string.Empty)} and '{spec.Relation.Value}'{(spec.IsDefault ? " (AsDefault)" : string.Empty)}. " +
                    "Mark only one always-emitted affordance AsDefault() — additional defaults are allowed when gated with When() or RequireAuthorization() so at most one emits per response — " +
                    "and don't combine an unconditional AsDefault() with an unconditional affordance named 'default'.");
            }

            claimant = spec;
        }
    }

    // Every policy is known once the config is compiled, so a typo can be caught at startup instead of as a
    // request-time failure when the link is first built.
    public IReadOnlyCollection<string> Policies
    {
        get
        {
            HashSet<string>? policies = null;
            foreach (var spec in _builder.LinkSpecs)
            {
                if (spec.Policy is { Length: > 0 } policy)
                {
                    (policies ??= new(StringComparer.Ordinal)).Add(policy);
                }
            }

            foreach (var spec in _builder.AffordanceSpecs)
            {
                if (spec.Policy is { Length: > 0 } policy)
                {
                    (policies ??= new(StringComparer.Ordinal)).Add(policy);
                }
            }

            return policies ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        }
    }

    // The declared embeds are fixed once the config is compiled; project them once (lazily) into the public
    // descriptor document generators read to type the _embedded schema.
    public IReadOnlyList<EmbeddedResourceSchema> EmbeddedResources
    {
        get
        {
            if (_embeddedResources is not null)
            {
                return _embeddedResources;
            }

            if (_builder.EmbedSpecs.Count == 0)
            {
                return _embeddedResources = Array.Empty<EmbeddedResourceSchema>();
            }

            var embeds = new EmbeddedResourceSchema[_builder.EmbedSpecs.Count];
            for (var i = 0; i < embeds.Length; i++)
            {
                var spec = _builder.EmbedSpecs[i];
                embeds[i] = new EmbeddedResourceSchema(spec.Relation, spec.ChildType, spec.Single);
            }

            return _embeddedResources = embeds;
        }
    }

    public async ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typed = (T)resource;

        var links = new List<Link>(_builder.LinkSpecs.Count);
        foreach (var spec in _builder.LinkSpecs)
        {
            if (!await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (spec.SingleTarget is { } single)
            {
                AddLink(links, spec, await single(typed, context).ConfigureAwait(false), context);
                continue;
            }

            var targets = await spec.Targets!(typed, context).ConfigureAwait(false) ?? [];
            foreach (var target in targets)
            {
                AddLink(links, spec, target, context);
            }
        }

        var affordances = new List<Affordance>(_builder.AffordanceSpecs.Count);
        foreach (var spec in _builder.AffordanceSpecs)
        {
            if (!await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var target = await spec.Target(typed, context).ConfigureAwait(false);
            if (ResolveHref(spec.Relation, target, context) is { } href)
            {
                affordances.Add(new Affordance(spec.Relation, href, spec.HttpMethod) { Title = target.Title ?? spec.TitleText, Input = spec.InputType, ContentType = spec.ContentTypeText, IsDefault = spec.IsDefault });
            }
        }

        List<EmbeddedResource>? embedded = null;
        foreach (var spec in _builder.EmbedSpecs)
        {
            var resources = spec.Resolve(typed);
            if (spec.Single && resources.Count == 0)
            {
                continue;   // a null single embed contributes nothing
            }

            (embedded ??= []).Add(new EmbeddedResource(spec.Relation, resources, spec.Single));
        }

        return links.Count == 0 && affordances.Count == 0 && (embedded is null || embedded.Count == 0)
            ? LinkSet.Empty
            : new LinkSet(links, affordances, embedded);
    }

    private static void AddLink(List<Link> links, LinkSpec<T> spec, LinkTarget target, LinkContext context)
    {
        if (ResolveHref(spec.Relation, target, context) is not { } href)
        {
            return;
        }

        var templated = target is ExplicitLinkTarget { Templated: true } or RouteTemplateLinkTarget;

        // Per-target attributes (LinkTarget.With*) override the spec-level ones, so members of a
        // multi-link relation can each carry their own name/title/hreflang/....
        links.Add(new Link(spec.Relation, href, templated)
        {
            Title = target.Title ?? spec.TitleText,
            Type = target.Type ?? spec.TypeText,
            Name = target.Name ?? spec.NameText,
            Deprecation = target.Deprecation ?? spec.DeprecationText,
            Hreflang = target.Hreflang ?? spec.HreflangText,
            Profile = target.Profile ?? spec.ProfileText,
        });
    }

    private static async ValueTask<bool> IncludeAsync(HypermediaSpec<T> spec, T resource, LinkContext context, CancellationToken cancellationToken)
    {
        if (spec.Condition is not null && !await spec.Condition(resource, context).ConfigureAwait(false))
        {
            return false;
        }

        if (spec.Policy is not null && !await context.Authorizer.AuthorizeAsync(spec.Policy, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static string? ResolveHref(LinkRelation relation, LinkTarget target, LinkContext context)
    {
        var href = context.UrlResolver.Resolve(target);

        // Treat null-or-whitespace as unresolved: Strict throws a clear LinkResolutionException; Lax drops the
        // link (rather than letting the Link/Affordance constructor throw a raw ArgumentException, which would
        // abort serialization and defeat Lax mode).
        if (string.IsNullOrWhiteSpace(href))
        {
            if (context.Mode == LinkResolutionMode.Strict)
            {
                throw new LinkResolutionException(
                    $"Could not resolve a URL for relation '{relation}' targeting {Describe(target)}. " +
                    "Ensure the endpoint is named (WithName / [Http*(Name=...)]) and all route values are supplied.");
            }

            context.OnUnresolvedLink?.Invoke(new UnresolvedLink(typeof(T), relation, target));
            return null;
        }

        return href;
    }

    private static string Describe(LinkTarget target) => target switch
    {
        RouteLinkTarget route => $"route '{route.RouteName}'",
        RouteTemplateLinkTarget template => $"route template '{template.RouteName}'",
        ExplicitLinkTarget uri => $"URI '{uri.Href}'",
        _ => "the target",
    };
}
