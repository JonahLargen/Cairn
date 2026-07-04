using Cairn.Testing;

namespace Cairn.AspNetCore.Tests;

// Parser edges of Cairn.Testing's HypermediaResponse: malformed member values are ignored or fail loudly
// (never silently mis-asserted), HAL-FORMS defaults are honored, and the assertion messages stay readable
// on an empty response.
public class CairnTestingParseEdgeTests
{
    [Fact]
    public void Constructing_a_response_from_links_and_affordances_defaults_the_optional_sections()
    {
        var links = new Dictionary<string, HypermediaLink> { ["self"] = new("/o/1", null) };
        var affordances = new Dictionary<string, HypermediaAffordance> { ["cancel"] = new("/o/1/cancel", "POST", null) };

        var response = new HypermediaResponse(links, affordances);

        // AllLinks mirrors Links one-to-one; Embedded and Templates start empty.
        Assert.Equal("/o/1", Assert.Single(response.AllLinks["self"]).Href);
        Assert.Empty(response.Embedded);
        Assert.Empty(response.Templates);
    }

    [Fact]
    public void A_template_with_a_non_string_target_fails_loudly()
    {
        const string json = """
            {"_templates":{"broken":{"target":123,"method":"POST"}}}
            """;

        var exception = Assert.Throws<FormatException>(() => HypermediaResponse.Parse(json));

        Assert.Contains("broken", exception.Message);
    }

    [Fact]
    public void A_template_without_a_target_or_self_link_gets_an_empty_target()
    {
        const string json = """
            {"_templates":{"create":{"method":"POST"}}}
            """;

        var response = HypermediaResponse.Parse(json);

        // HAL-FORMS says an absent target means "submit to the resource itself"; with no self link there is
        // nothing to fall back to, so the target stays empty rather than failing the parse.
        Assert.Equal(string.Empty, response.Templates["create"].Target);
    }

    [Fact]
    public void A_template_without_a_target_falls_back_to_the_self_link()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"}},"_templates":{"update":{"method":"PUT"}}}
            """;

        Assert.Equal("/o/1", HypermediaResponse.Parse(json).Templates["update"].Target);
    }

    [Fact]
    public void Embedded_values_parse_single_objects_and_arrays_and_skip_scalars()
    {
        const string json = """
            {
                "_embedded": {
                    "customer": {"_links": {"self": {"href": "/c/1"}}},
                    "items": [{"_links": {"self": {"href": "/i/1"}}}, {"_links": {"self": {"href": "/i/2"}}}],
                    "broken": "not-a-resource"
                }
            }
            """;

        var response = HypermediaResponse.Parse(json);

        Assert.Equal("/c/1", Assert.Single(response.Embedded["customer"]).Links["self"].Href);
        Assert.Equal(2, response.Embedded["items"].Count);
        Assert.False(response.Embedded.ContainsKey("broken"));
    }

    [Fact]
    public void Non_string_link_members_read_as_absent()
    {
        // A numeric title/type is malformed decoration on an otherwise valid link; the link still parses.
        const string json = """
            {"_links":{"self":{"href":"/o/1","title":5,"type":17,"templated":"yes"}}}
            """;

        var link = HypermediaResponse.Parse(json).Links["self"];

        Assert.Null(link.Title);
        Assert.Null(link.Type);
        Assert.False(link.Templated);
    }

    [Fact]
    public void An_explicit_templated_false_parses_as_not_templated()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1","templated":false}}}
            """;

        Assert.False(HypermediaResponse.Parse(json).Links["self"].Templated);
    }

    [Fact]
    public void Malformed_field_constraints_read_as_absent()
    {
        const string json = """
            {
                "_templates": {
                    "create": {
                        "target": "/o",
                        "method": "POST",
                        "properties": [
                            {"name": "note", "maxLength": "long", "min": "low", "max": 10.5, "required": "yes"},
                            {"name": "qty", "maxLength": 2147483648}
                        ]
                    }
                }
            }
            """;

        var fields = HypermediaResponse.Parse(json).Templates["create"].Fields;

        // Non-numeric (or non-int32) constraints and non-boolean flags are dropped, not coerced.
        Assert.Null(fields[0].MaxLength);
        Assert.Null(fields[0].Min);
        Assert.Equal(10.5, fields[0].Max);
        Assert.False(fields[0].Required);
        Assert.Null(fields[1].MaxLength);
    }

    [Fact]
    public void Inline_options_accept_bare_strings_and_value_objects_and_skip_the_rest()
    {
        const string json = """
            {
                "_templates": {
                    "create": {
                        "target": "/o",
                        "method": "POST",
                        "properties": [
                            {"name": "status", "options": {"inline": ["open", {"value": "closed"}, 7, {"value": 9}]}}
                        ]
                    }
                }
            }
            """;

        var field = Assert.Single(HypermediaResponse.Parse(json).Templates["create"].Fields);

        Assert.Equal(new[] { "open", "closed" }, field.Options);
    }

    [Fact]
    public void Missing_link_message_says_none_when_the_response_has_no_links()
    {
        var exception = Assert.Throws<CairnAssertionException>(
            () => HypermediaResponse.Parse("{}").Should().HaveLink("self"));

        Assert.Contains("its links are: none", exception.Message);
    }

    [Fact]
    public void Template_assertions_chain_back_to_the_response()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"}},"_templates":{"update":{"target":"/o/1","method":"PUT"}}}
            """;

        HypermediaResponse.Parse(json).Should()
            .HaveTemplate("update").WithMethod(HttpMethod.Put)
            .And.HaveLink("self", "/o/1");
    }
}
