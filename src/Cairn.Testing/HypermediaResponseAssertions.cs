using System.Text;
using System.Text.RegularExpressions;

namespace Cairn.Testing;

/// <summary>Entry point for fluent assertions over a <see cref="HypermediaResponse"/>.</summary>
public static class HypermediaResponseAssertionsExtensions
{
    /// <summary>Returns fluent assertions for the hypermedia response.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static HypermediaResponseAssertions Should(this HypermediaResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new HypermediaResponseAssertions(response);
    }
}

// Failed expectations throw CairnAssertionException with a message describing both the expectation and
// the actual hypermedia, so the assertions work under any test framework without a third-party library.
internal static class CairnAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition)
        {
            throw new CairnAssertionException(message);
        }
    }

    public static string Describe(IEnumerable<string> keys)
    {
        var list = string.Join(", ", keys.Select(key => $"'{key}'"));
        return list.Length == 0 ? "none" : list;
    }
}

// Compiles an href pattern to a regex: "{param}" matches one path segment, a trailing "*" turns the pattern
// into a prefix match, and everything else matches literally (regex-escaped), anchored ^...$.
internal static class HrefPattern
{
    public static Regex ToRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var prefix = pattern.EndsWith('*');
        if (prefix)
        {
            pattern = pattern[..^1];
        }

        var builder = new StringBuilder("^");
        var index = 0;
        while (index < pattern.Length)
        {
            var open = pattern.IndexOf('{', index);
            var close = open < 0 ? -1 : pattern.IndexOf('}', open);
            if (close < 0)
            {
                builder.Append(Regex.Escape(pattern[index..]));
                break;
            }

            builder.Append(Regex.Escape(pattern[index..open]));
            builder.Append("[^/?#]+");   // a {param} placeholder matches exactly one path segment
            index = close + 1;
        }

        builder.Append(prefix ? string.Empty : "$");
        return new Regex(builder.ToString(), RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}

/// <summary>Fluent assertions over the links, affordances, templates, and embedded resources of a <see cref="HypermediaResponse"/>.</summary>
public sealed class HypermediaResponseAssertions
{
    private readonly HypermediaResponse _subject;

    internal HypermediaResponseAssertions(HypermediaResponse subject) => _subject = subject;

    /// <summary>Continues a chain of assertions.</summary>
    public HypermediaResponseAssertions And => this;

    /// <summary>Asserts the response exposes a <c>self</c> link.</summary>
    public HypermediaResponseAssertions HaveSelfLink() => HaveLink("self");

    /// <summary>Asserts the response exposes a link with the given relation.</summary>
    public HypermediaResponseAssertions HaveLink(string relation)
    {
        CairnAssert.That(
            _subject.Links.ContainsKey(relation),
            $"Expected the response to expose a '{relation}' link, but its links are: {CairnAssert.Describe(_subject.Links.Keys)}.");
        return this;
    }

    /// <summary>Asserts the response exposes a link with the given relation and href (any member of a link array may match).</summary>
    public HypermediaResponseAssertions HaveLink(string relation, string href)
    {
        HaveLink(relation);
        var links = _subject.AllLinks[relation];
        if (links.Count == 1)
        {
            CairnAssert.That(
                links[0].Href == href,
                $"Expected the '{relation}' link to have href '{href}', but it is '{links[0].Href}'.");
        }
        else
        {
            CairnAssert.That(
                links.Any(link => link.Href == href),
                $"Expected one of the '{relation}' links to have href '{href}', but they are: {CairnAssert.Describe(links.Select(link => link.Href))}.");
        }

        return this;
    }

    /// <summary>
    /// Asserts the response exposes a link with the given relation whose href matches <paramref name="pattern"/>
    /// (any member of a link array may match). In the pattern, <c>{param}</c> matches exactly one path segment
    /// and a trailing <c>*</c> makes it a prefix match, so tests stay robust against the test host's
    /// scheme/host/port (e.g. <c>"http://{host}/orders/{id}"</c> or <c>"http://localhost/orders/*"</c>).
    /// </summary>
    public HypermediaResponseAssertions HaveLinkMatching(string relation, string pattern)
    {
        HaveLink(relation);
        var regex = HrefPattern.ToRegex(pattern);
        var links = _subject.AllLinks[relation];
        CairnAssert.That(
            links.Any(link => regex.IsMatch(link.Href)),
            links.Count == 1
                ? $"Expected the '{relation}' link's href to match '{pattern}', but it is '{links[0].Href}'."
                : $"Expected one of the '{relation}' links to match '{pattern}', but they are: {CairnAssert.Describe(links.Select(link => link.Href))}.");
        return this;
    }

    /// <summary>Asserts the response exposes a link with the given relation whose href is a URI template (<c>"templated": true</c>).</summary>
    public HypermediaResponseAssertions HaveTemplatedLink(string relation)
    {
        HaveLink(relation);
        CairnAssert.That(
            _subject.AllLinks[relation].Any(link => link.Templated),
            $"Expected the '{relation}' link to be templated (\"templated\": true), but it is not.");
        return this;
    }

    /// <summary>Asserts the response does not expose a link with the given relation.</summary>
    public HypermediaResponseAssertions NotHaveLink(string relation)
    {
        CairnAssert.That(
            !_subject.Links.TryGetValue(relation, out var link),
            $"Expected the response not to expose a '{relation}' link, but it does (href '{link?.Href}').");
        return this;
    }

    /// <summary>Asserts the response exposes the named affordance, returning assertions for it.</summary>
    public HypermediaAffordanceAssertions HaveAffordance(string name)
    {
        CairnAssert.That(
            _subject.Affordances.ContainsKey(name),
            $"Expected the response to expose the '{name}' affordance, but its affordances are: {CairnAssert.Describe(_subject.Affordances.Keys)}.");
        return new HypermediaAffordanceAssertions(this, name, _subject.Affordances[name]);
    }

    /// <summary>Asserts the response does not expose the named affordance.</summary>
    public HypermediaResponseAssertions NotHaveAffordance(string name)
    {
        CairnAssert.That(
            !_subject.Affordances.ContainsKey(name),
            $"Expected the response not to expose the '{name}' affordance, but it does.");
        return this;
    }

    /// <summary>
    /// Asserts the response embeds a resource under the given relation, returning assertions for it.
    /// A collection embed asserts on its first resource; use <see cref="HypermediaResponse.Embedded"/> for the full set.
    /// </summary>
    public HypermediaResponseAssertions HaveEmbedded(string relation)
    {
        CairnAssert.That(
            _subject.Embedded.ContainsKey(relation),
            $"Expected the response to embed a '{relation}' resource, but its embedded relations are: {CairnAssert.Describe(_subject.Embedded.Keys)}.");
        return new HypermediaResponseAssertions(_subject.Embedded[relation][0]);
    }

    /// <summary>Asserts the response exposes the named HAL-FORMS template, returning assertions for it.</summary>
    public HypermediaTemplateAssertions HaveTemplate(string name)
    {
        CairnAssert.That(
            _subject.Templates.ContainsKey(name),
            $"Expected the response to expose the '{name}' template, but its templates are: {CairnAssert.Describe(_subject.Templates.Keys)}.");
        return new HypermediaTemplateAssertions(this, name, _subject.Templates[name]);
    }
}

/// <summary>Fluent assertions over a single affordance.</summary>
public sealed class HypermediaAffordanceAssertions
{
    private readonly HypermediaResponseAssertions _parent;
    private readonly string _name;
    private readonly HypermediaAffordance _affordance;

    internal HypermediaAffordanceAssertions(HypermediaResponseAssertions parent, string name, HypermediaAffordance affordance)
    {
        _parent = parent;
        _name = name;
        _affordance = affordance;
    }

    /// <summary>Continues asserting on the response.</summary>
    public HypermediaResponseAssertions And => _parent;

    /// <summary>Asserts the affordance is invoked with the given HTTP method.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
    public HypermediaAffordanceAssertions WithMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        CairnAssert.That(
            _affordance.Method == method.Method,
            $"Expected the '{_name}' affordance to use method '{method.Method}', but it uses '{_affordance.Method}'.");
        return this;
    }

    /// <summary>Asserts the affordance targets the given href.</summary>
    public HypermediaAffordanceAssertions WithHref(string href)
    {
        CairnAssert.That(
            _affordance.Href == href,
            $"Expected the '{_name}' affordance to target '{href}', but it targets '{_affordance.Href}'.");
        return this;
    }

    /// <summary>
    /// Asserts the affordance's target href matches <paramref name="pattern"/>, where <c>{param}</c> matches
    /// exactly one path segment and a trailing <c>*</c> makes it a prefix match.
    /// </summary>
    public HypermediaAffordanceAssertions WithHrefMatching(string pattern)
    {
        CairnAssert.That(
            HrefPattern.ToRegex(pattern).IsMatch(_affordance.Href),
            $"Expected the '{_name}' affordance's target to match '{pattern}', but it targets '{_affordance.Href}'.");
        return this;
    }
}

/// <summary>Fluent assertions over a single HAL-FORMS template.</summary>
public sealed class HypermediaTemplateAssertions
{
    private readonly HypermediaResponseAssertions _parent;
    private readonly string _name;
    private readonly HypermediaTemplate _template;

    internal HypermediaTemplateAssertions(HypermediaResponseAssertions parent, string name, HypermediaTemplate template)
    {
        _parent = parent;
        _name = name;
        _template = template;
    }

    /// <summary>Continues asserting on the response.</summary>
    public HypermediaResponseAssertions And => _parent;

    /// <summary>Asserts the template is submitted with the given HTTP method.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
    public HypermediaTemplateAssertions WithMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        CairnAssert.That(
            _template.Method == method.Method,
            $"Expected the '{_name}' template to use method '{method.Method}', but it uses '{_template.Method}'.");
        return this;
    }

    /// <summary>Asserts the template is submitted to the given target URI.</summary>
    public HypermediaTemplateAssertions WithTarget(string target)
    {
        CairnAssert.That(
            _template.Target == target,
            $"Expected the '{_name}' template to target '{target}', but it targets '{_template.Target}'.");
        return this;
    }

    /// <summary>
    /// Asserts the template's submission target matches <paramref name="pattern"/>, where <c>{param}</c>
    /// matches exactly one path segment and a trailing <c>*</c> makes it a prefix match.
    /// </summary>
    public HypermediaTemplateAssertions WithTargetMatching(string pattern)
    {
        CairnAssert.That(
            HrefPattern.ToRegex(pattern).IsMatch(_template.Target),
            $"Expected the '{_name}' template's target to match '{pattern}', but it targets '{_template.Target}'.");
        return this;
    }

    /// <summary>Asserts the template is submitted as the given media type.</summary>
    public HypermediaTemplateAssertions WithContentType(string contentType)
    {
        CairnAssert.That(
            _template.ContentType == contentType,
            $"Expected the '{_name}' template to accept content type '{contentType}', but it accepts '{_template.ContentType}'.");
        return this;
    }

    /// <summary>Asserts the template has a field with the given name, returning assertions for it.</summary>
    public HypermediaTemplateFieldAssertions HaveField(string name)
    {
        var match = _template.Fields.FirstOrDefault(field => field.Name == name);
        CairnAssert.That(
            match is not null,
            $"Expected the '{_name}' template to have a '{name}' field, but its fields are: {CairnAssert.Describe(_template.Fields.Select(field => field.Name))}.");
        return new HypermediaTemplateFieldAssertions(this, _name, match!);
    }
}

/// <summary>Fluent assertions over a single HAL-FORMS template field.</summary>
public sealed class HypermediaTemplateFieldAssertions
{
    private readonly HypermediaTemplateAssertions _parent;
    private readonly string _templateName;
    private readonly HypermediaTemplateField _field;

    internal HypermediaTemplateFieldAssertions(HypermediaTemplateAssertions parent, string templateName, HypermediaTemplateField field)
    {
        _parent = parent;
        _templateName = templateName;
        _field = field;
    }

    /// <summary>Continues asserting on the template.</summary>
    public HypermediaTemplateAssertions And => _parent;

    /// <summary>Asserts the field is required.</summary>
    public HypermediaTemplateFieldAssertions ThatIsRequired()
    {
        CairnAssert.That(
            _field.Required,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to be required, but it is optional.");
        return this;
    }

    /// <summary>Asserts the field is optional (not required).</summary>
    public HypermediaTemplateFieldAssertions ThatIsOptional()
    {
        CairnAssert.That(
            !_field.Required,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to be optional, but it is required.");
        return this;
    }

    /// <summary>Asserts the field is read-only.</summary>
    public HypermediaTemplateFieldAssertions ThatIsReadOnly()
    {
        CairnAssert.That(
            _field.ReadOnly,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to be read-only, but it is not.");
        return this;
    }

    /// <summary>Asserts the field has the given input type.</summary>
    public HypermediaTemplateFieldAssertions WithType(string type)
    {
        CairnAssert.That(
            _field.Type == type,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to have type '{type}', but it has '{_field.Type}'.");
        return this;
    }

    /// <summary>Asserts the field's value must match the given regular expression.</summary>
    public HypermediaTemplateFieldAssertions WithRegex(string pattern)
    {
        CairnAssert.That(
            _field.Regex == pattern,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to have regex '{pattern}', but it has '{_field.Regex}'.");
        return this;
    }

    /// <summary>Asserts the field has the given prompt.</summary>
    public HypermediaTemplateFieldAssertions WithPrompt(string prompt)
    {
        CairnAssert.That(
            _field.Prompt == prompt,
            $"Expected the '{_field.Name}' field of the '{_templateName}' template to have prompt '{prompt}', but it has '{_field.Prompt}'.");
        return this;
    }
}
