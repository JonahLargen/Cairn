using System.Collections.Immutable;
using Cairn.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cairn.Analyzers.Tests;

public class RoutesGeneratorBranchTests
{
    [Fact]
    public void An_absolute_action_template_overrides_the_controller_prefix()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
}

[Route(""api/{version:int}"")]
class ThingsController
{
    [HttpGet(""/things/{id:int}"", Name = ""GetAbsoluteThing"")]
    public object Get(int id) => null;
}";

        var generated = Run(source);

        // A '/'-rooted action template is absolute; the controller prefix's {version} must not appear.
        Assert.Contains("public static global::Cairn.LinkTarget GetAbsoluteThing(int id)", generated);
        Assert.DoesNotContain("version", generated);
    }

    [Fact]
    public void Non_string_attribute_constructor_arguments_contribute_no_template()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class HttpGetAttribute : System.Attribute
    {
        public HttpGetAttribute(int order) {}
        public HttpGetAttribute(System.Type marker) {}
        public string Name { get; set; }
    }
}

class OddController
{
    [HttpGet(5, Name = ""OrderedRoute"")]
    public object Ordered() => null;

    [HttpGet(typeof(object), Name = ""TypedRoute"")]
    public object Typed() => null;
}";

        var generated = Run(source);

        // A first constructor argument that isn't a string (a primitive int, a non-primitive typeof) is not
        // a route template; the route still lands in the catalog with no parameters.
        Assert.Contains("public static global::Cairn.LinkTarget OrderedRoute()", generated);
        Assert.Contains("public static global::Cairn.LinkTarget TypedRoute()", generated);
    }

    [Fact]
    public void A_with_name_argument_that_is_not_a_constant_string_is_skipped()
    {
        const string source = @"
class Program
{
    static void M(string dynamicName)
    {
        app.MapGet(""/a/{id:int}"", handler).WithName(dynamicName);
        app.MapGet(""/b/{id:int}"", handler).WithName(null);
        app.MapGet(""/c/{id:int}"", handler).WithName(""Kept"");
    }
}";

        var generated = Run(source);

        // A name only known at runtime (or the null literal, which is constant but not a string) cannot be
        // a catalog method; only the literal-named endpoint survives.
        Assert.Contains("public static global::Cairn.LinkTarget Kept(int id)", generated);
        Assert.Single(SplitMethods(generated));
    }

    [Fact]
    public void A_with_name_whose_chain_has_no_route_template_is_skipped()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.UseRouting().WithName(""NotARoute"");
        app.MapGet(""/kept"", handler).WithName(""Kept"");
    }
}";

        var generated = Run(source);

        // A WithName whose fluent chain never passes a Map* call has no template — nothing to generate.
        Assert.DoesNotContain("NotARoute", generated);
        Assert.Contains("public static global::Cairn.LinkTarget Kept()", generated);
    }

    [Fact]
    public void Includes_every_map_verb_and_walks_through_intermediate_builder_calls()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapPut(""/put/{id:int}"", handler).WithName(""PutThing"");
        app.MapDelete(""/delete/{id:int}"", handler).WithName(""DeleteThing"");
        app.MapPatch(""/patch/{id:int}"", handler).WithName(""PatchThing"");
        app.MapMethods(""/methods/{id:int}"", verbs, handler).WithName(""MultiThing"");
        app.MapGet(""/tagged/{id:int}"", handler).WithTags(""t"").RequireAuthorization().WithName(""TaggedThing"");
    }
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget PutThing(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget DeleteThing(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget PatchThing(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget MultiThing(int id)", generated);
        // Non-Map builder calls between the endpoint and WithName are walked through, not templates.
        Assert.Contains("public static global::Cairn.LinkTarget TaggedThing(int id)", generated);
    }

    [Fact]
    public void A_map_call_without_a_string_pattern_yields_no_route()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.Map(handler).WithName(""NoTemplate"");
        app.MapFallback().WithName(""EmptyFallback"");
    }
}";

        var generated = Run(source);

        // Map with a non-string first argument has no template; only MapFallback gets the implicit one —
        // and its zero-argument overload exercises the empty-argument-list path.
        Assert.DoesNotContain("NoTemplate", generated);
        Assert.Contains("public static global::Cairn.LinkTarget EmptyFallback(string path)", generated);
    }

    [Fact]
    public void A_map_group_with_a_non_constant_prefix_contributes_no_parameters()
    {
        const string source = @"
class Program
{
    static void M(string prefix)
    {
        app.MapGroup(prefix).MapGet(""/items/{id:int}"", handler).WithName(""GetDynamicItem"");
    }
}";

        var generated = Run(source);

        // A prefix only known at runtime cannot contribute parameters; the endpoint's own template still can.
        Assert.Contains("public static global::Cairn.LinkTarget GetDynamicItem(int id)", generated);
    }

    [Fact]
    public void A_with_name_directly_on_a_map_group_is_not_a_route()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGroup(""/orders"").WithName(""OrdersGroup"");
        app.MapGet(""/kept"", handler).WithName(""Kept"");
    }
}";

        var generated = Run(source);

        // A group has no endpoint template of its own — naming it does not create a catalog entry.
        Assert.DoesNotContain("OrdersGroup", generated);
        Assert.Contains("public static global::Cairn.LinkTarget Kept()", generated);
    }

    [Fact]
    public void A_cyclic_variable_chain_is_depth_bounded()
    {
        const string source = @"
class Program
{
    static void M()
    {
        var g = g.MapGroup(""/loop"");
        g.MapGet(""/items/{id:int}"", handler).WithName(""GetLoopedItem"");
    }
}";

        var generated = Run(source);

        // A (nonsensical but parseable) self-referential variable must hit the depth bound, not recurse forever.
        Assert.Contains("public static global::Cairn.LinkTarget GetLoopedItem(int id)", generated);
    }

    [Fact]
    public void A_chain_bottoming_out_at_a_non_identifier_expression_stops_there()
    {
        const string source = @"
class Program
{
    static void M()
    {
        CreateApp().MapGet(""/factory/{id:int}"", handler).WithName(""FromFactory"");
    }
}";

        var generated = Run(source);

        // The chain ends at CreateApp() — an invocation, not an identifier — so there is no variable
        // initializer to follow; the endpoint's own template still generates.
        Assert.Contains("public static global::Cairn.LinkTarget FromFactory(int id)", generated);
    }

    [Fact]
    public void Resolves_a_group_variable_referenced_from_an_expression_bodied_local_function()
    {
        const string source = @"
class Program
{
    static void M()
    {
        var g = app.MapGroup(""/users/{userId:int}"");
        object Local() => g.MapGet(""/orders/{id:int}"", handler).WithName(""GetViaLocalFunction"");
    }
}";

        var generated = Run(source);

        // An expression-bodied local function has no block, so the scope walk must pass through the
        // LocalFunctionStatement itself before finding the declarator in the enclosing method body.
        Assert.Contains("public static global::Cairn.LinkTarget GetViaLocalFunction(int userId, int id)", generated);
    }

    [Fact]
    public void An_escaped_brace_pair_is_a_literal_not_a_parameter()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/literal/{{escaped}}/{id:int}"", handler).WithName(""GetEscaped"");
    }
}";

        var generated = Run(source);

        // ""{{escaped}}"" is an escaped literal brace pair; only {id} is a parameter.
        Assert.Contains("public static global::Cairn.LinkTarget GetEscaped(int id)", generated);
        Assert.DoesNotContain("escaped", generated);
    }

    [Fact]
    public void An_unclosed_brace_ends_parameter_parsing()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/orders/{id:int}/bad{rest"", handler).WithName(""GetMalformed"");
        app.MapGet(""/trailing{"", handler).WithName(""GetTrailingBrace"");
    }
}";

        var generated = Run(source);

        // Parameters before the unmatched '{' survive; the malformed tail (including a template that ends
        // on the '{' itself) is abandoned rather than throwing.
        Assert.Contains("public static global::Cairn.LinkTarget GetMalformed(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetTrailingBrace()", generated);
        Assert.DoesNotContain("rest", generated);
    }

    [Fact]
    public void Parameters_that_are_not_valid_identifiers_are_skipped()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/odd/{9lives}/{}/{a-b}/{_ok:int}"", handler).WithName(""GetOdd"");
    }
}";

        var generated = Run(source);

        // A digit-first name, an empty name, and a name with a non-identifier character can't be C#
        // parameters; the underscore-first one can.
        Assert.Contains("public static global::Cairn.LinkTarget GetOdd(int _ok)", generated);
        Assert.DoesNotContain("9lives", generated);
        Assert.DoesNotContain("a-b", generated);
    }

    [Fact]
    public void Maps_every_route_constraint_to_its_clr_type()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/all/{a:long}/{b:bool}/{c:double}/{d:float}/{e:decimal}/{f:datetime}/{g:max(9)}/{h:range(1,5)}"", handler).WithName(""GetAllConstraints"");
    }
}";

        var generated = Run(source);

        Assert.Contains(
            "public static global::Cairn.LinkTarget GetAllConstraints(long a, bool b, double c, float d, decimal e, global::System.DateTime f, long g, long h)",
            generated);
    }

    [Fact]
    public void A_route_name_with_no_identifier_characters_is_skipped()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/punctuation"", handler).WithName(""!!!"");
        app.MapGet(""/kept"", handler).WithName(""Kept"");
    }
}";

        var generated = Run(source);

        // A name that sanitizes to nothing has no possible method name; it is dropped silently.
        Assert.Contains("public static global::Cairn.LinkTarget Kept()", generated);
        Assert.Single(SplitMethods(generated));
    }

    [Fact]
    public void A_route_name_starting_with_a_digit_is_underscore_prefixed()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/first"", handler).WithName(""1st-route"");
    }
}";

        var generated = Run(source);

        // '1stRoute' is not a legal method name; the generator prefixes it rather than failing the build.
        Assert.Contains("public static global::Cairn.LinkTarget _1stRoute()", generated);
        AssertCatalogCompiles(generated);
    }

    [Fact]
    public void An_exactly_repeated_route_name_is_dropped_without_a_collision_diagnostic()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/a"", handler).WithName(""GetThing"");
        app.MapGet(""/b"", handler).WithName(""GetThing"");
    }
}";

        var result = RunDriver(source);
        var generated = result.GeneratedTrees.Single().ToString();

        // CAIRN003 exists to surface two *distinct* names reducing to one method; the same name twice is
        // an ASP.NET routing concern, not a catalog collision.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CAIRN003");
        Assert.Single(SplitMethods(generated));
    }

    [Fact]
    public void Keyword_parameter_names_are_escaped()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/keywords/{class}/{async}"", handler).WithName(""GetKeywords"");
    }
}";

        var generated = Run(source);

        // 'class' is a reserved keyword and 'async' a contextual one; both need the '@' escape to compile.
        Assert.Contains("public static global::Cairn.LinkTarget GetKeywords(string @class, string @async)", generated);
        AssertCatalogCompiles(generated);
    }

    [Fact]
    public void A_non_route_class_attribute_does_not_provide_a_prefix_and_property_attributes_are_ignored()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute() {} public HttpGetAttribute(string template) {} public string Name { get; set; } }
    class ApiControllerAttribute : System.Attribute { }
}

[ApiController]
[Route(""api"")]
class MixedController
{
    [HttpGet(""things/{id:int}"", Name = ""GetMixedThing"")]
    public object Get(int id) => null;

    // An attribute target that is neither a method nor a type never reaches the extractor.
    [HttpGet(Name = ""FromProperty"")]
    public object NotAnAction => null;
}";

        var generated = Run(source);

        // The prefix walk must step over [ApiController] to find [Route(""api"")].
        Assert.Contains("public static global::Cairn.LinkTarget GetMixedThing(int id)", generated);
        Assert.DoesNotContain("FromProperty", generated);
    }

    [Fact]
    public void An_ambiguous_route_attribute_does_not_break_prefix_resolution()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
using Other;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
}
namespace Other
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} }
}

[Route(""apix"")]
class AmbiguousController
{
    [HttpGet(""things/{id:int}"", Name = ""AmbiguousPrefixRoute"")]
    public object Get(int id) => null;
}";

        var generated = Run(source);

        // [Route] binds to neither candidate; whichever way the compiler models the failure, the action's
        // own template must still generate.
        Assert.Contains("public static global::Cairn.LinkTarget AmbiguousPrefixRoute(int id)", generated);
    }

    private static string Run(string source) => RunDriver(source).GeneratedTrees.Single().ToString();

    // Every generated catalog method starts with this exact prefix, so counting occurrences counts methods.
    private static IEnumerable<int> SplitMethods(string generated)
    {
        for (var index = generated.IndexOf("public static global::Cairn.LinkTarget", StringComparison.Ordinal);
             index >= 0;
             index = generated.IndexOf("public static global::Cairn.LinkTarget", index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    // Compiles the generated catalog against a LinkTarget stub, so name-binding errors the parser can't see
    // (e.g. an unescaped keyword parameter) fail the test.
    private static void AssertCatalogCompiles(string generated)
    {
        const string stub = @"
namespace Cairn
{
    public sealed class LinkTarget
    {
        public static LinkTarget Route(string routeName, object? values) => new LinkTarget();
    }
}";
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(
            "GeneratedCatalog",
            [CSharpSyntaxTree.ParseText(generated), CSharpSyntaxTree.ParseText(stub)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Collections.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static GeneratorDriverRunResult RunDriver(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RoutesGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult();
    }
}
