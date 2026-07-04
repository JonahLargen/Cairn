using System.Collections.Immutable;
using Cairn.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class MissingLinkConfigAnalyzerBranchTests
{
    // The WithLinks stub lives in a Cairn.* namespace and [CairnLinks] mirrors Cairn.AspNetCore's attribute:
    // the analyzer binds both symbols and ignores same-named members from other libraries.
    private const string Stubs = @"
using Cairn.AspNetCore;
namespace Cairn { public abstract class LinkConfig<T> { } }
namespace Cairn.AspNetCore
{
    public sealed class CairnLinksAttribute : System.Attribute { }
    public static class Endpoints
    {
        public static object MapGet(this object app, string pattern) => app;
        public static object MapGet(this object app, string pattern, System.Delegate handler) => app;
        public static object MapGroup(this object app, string prefix) => app;
        public static T WithName<T>(this T builder, string name) => builder;
        public static T WithLinks<T>(this T builder) => builder;
    }
}
";

    [Fact]
    public async Task A_public_method_without_cairn_links_is_not_a_hypermedia_action()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
public class OrdersService
{
    public OrderDto Get(int id) => new OrderDto(id);
}");

        // Neither the method nor any containing type carries [CairnLinks]; the return type never collects.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task The_cairn_links_attribute_is_inherited_from_a_base_controller()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
[Cairn.AspNetCore.CairnLinks]
public class HypermediaControllerBase
{
}
public class OrdersController : HypermediaControllerBase
{
    public OrderDto Get(int id) => new OrderDto(id);
}");

        // The base-type walk must find the attribute one level up, exactly as MVC attribute inheritance does.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_look_alike_cairn_links_attribute_from_another_namespace_does_not_opt_in()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Other { public sealed class CairnLinksAttribute : System.Attribute { } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
public class OrdersController
{
    [Other.CairnLinks]
    public OrderDto Get(int id) => new OrderDto(id);
}");

        // Same name, wrong namespace — not Cairn's opt-in.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_variable_chain_deeper_than_the_depth_bound_is_abandoned()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        var e0 = app.MapGet(""/orders"", () => new OrderDto(1));
        var e1 = e0.WithName(""n1"");
        var e2 = e1.WithName(""n2"");
        var e3 = e2.WithName(""n3"");
        var e4 = e3.WithName(""n4"");
        var e5 = e4.WithName(""n5"");
        var e6 = e5.WithName(""n6"");
        var e7 = e6.WithName(""n7"");
        var e8 = e7.WithName(""n8"");
        e8.WithLinks();
    }
}");

        // Nine variable hops put the Map* call past the depth bound; the walk gives up rather than flag
        // (or loop on a pathological chain).
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task With_links_on_an_unresolvable_receiver_is_skipped()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        // A bare parameter has no initializer to follow; a literal receiver is not an identifier at all;
        // an undefined receiver leaves the call with no symbol and no candidates.
        app.WithLinks();
        ""not-a-builder"".WithLinks();
        undefined.WithLinks();
    }
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Resolves_an_endpoint_variable_from_an_expression_bodied_local_function()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        var endpoint = app.MapGet(""/orders"", () => new OrderDto(1));
        object Local() => endpoint.WithLinks();
        Local();
    }
}");

        // An expression-bodied local function has no block, so the scope walk must pass through the
        // LocalFunctionStatement itself before finding the declarator in the enclosing method body.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_method_group_handler_is_unwrapped()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    static OrderDto Handler() => new OrderDto(1);
    void Endpoints(object app) => app.MapGet(""/orders"", Handler).WithLinks();
}");

        // Handlers are as often method groups as lambdas; the group's return type is what serializes.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_map_call_without_a_recognizable_handler_is_skipped()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/orders"").WithLinks();
        // An explicit type argument makes the name a GenericNameSyntax; the pre-filter must still match.
        app.MapGet(""/customers"", () => new CustomerDto(1)).WithLinks<object>();
    }
}");

        // The pattern-only MapGet overload has no delegate argument — no return type to inspect.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Unwraps_array_return_types_to_the_element_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto[1]).WithLinks();
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task String_object_and_dynamic_returns_are_never_candidates()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/text"", () => ""hello"").WithLinks();
        app.MapGet(""/anything"", () => (object)null).WithLinks();
        app.MapGet(""/dynamic"", () => (dynamic)null).WithLinks();
    }
}");

        // string/object serialize as-is and dynamic is not a named type; none can carry a LinkConfig.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Unwraps_value_task_http_results_action_result_and_paging_envelopes()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Microsoft.AspNetCore.Http.HttpResults { public class Ok<T> { } }
namespace Microsoft.AspNetCore.Mvc { public class ActionResult<T> { } }
namespace Cairn.AspNetCore { public class PagedResource<T> { } public class CursorPage<T> { } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/vt"", () => default(System.Threading.Tasks.ValueTask<OrderDto>)).WithLinks();
        app.MapGet(""/ok"", () => default(Microsoft.AspNetCore.Http.HttpResults.Ok<OrderDto>)).WithLinks();
        app.MapGet(""/ar"", () => default(Microsoft.AspNetCore.Mvc.ActionResult<OrderDto>)).WithLinks();
        app.MapGet(""/paged"", () => default(Cairn.AspNetCore.PagedResource<OrderDto>)).WithLinks();
        app.MapGet(""/cursor"", () => default(Cairn.AspNetCore.CursorPage<OrderDto>)).WithLinks();
    }
}");

        // Each wrapper peels down to the OrderDto payload, and each endpoint reports it once.
        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Contains("OrderDto", d.GetMessage()));
    }

    [Fact]
    public async Task Unwraps_every_sequence_interface_to_the_element_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/e"", () => default(System.Collections.Generic.IEnumerable<OrderDto>)).WithLinks();
        app.MapGet(""/rl"", () => default(System.Collections.Generic.IReadOnlyList<OrderDto>)).WithLinks();
        app.MapGet(""/rc"", () => default(System.Collections.Generic.IReadOnlyCollection<OrderDto>)).WithLinks();
        app.MapGet(""/l"", () => default(System.Collections.Generic.IList<OrderDto>)).WithLinks();
        app.MapGet(""/c"", () => default(System.Collections.Generic.ICollection<OrderDto>)).WithLinks();
    }
}");

        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Contains("OrderDto", d.GetMessage()));
    }

    [Fact]
    public async Task Structs_and_anonymous_types_are_not_candidates()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record CustomerDto(int Id);
public struct OrderStruct { public int Id; }
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/struct"", () => new OrderStruct()).WithLinks();
        app.MapGet(""/anon"", () => new { Id = 1 }).WithLinks();
    }
}");

        // Links correlate by reference: value types and anonymous types can never be LinkConfig subjects.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Types_in_system_or_microsoft_namespaces_are_not_candidates()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace System.Custom { public class SystemDto { } }
namespace Microsoft.Custom { public class MicrosoftDto { } }
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        app.MapGet(""/sys"", () => new System.Custom.SystemDto()).WithLinks();
        app.MapGet(""/ms"", () => new Microsoft.Custom.MicrosoftDto()).WithLinks();
    }
}");

        // Framework-shaped namespaces are never LinkConfig subjects.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Types_from_the_msbuild_property_suppress_cross_project_references()
    {
        var source = Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}";

        var suppressed = await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["build_property.CairnAdditionalConfiguredTypes"] = "OrderDto",
        });

        // The CompilerVisibleProperty spelling must work exactly like the .editorconfig key.
        Assert.Empty(suppressed);
    }

    [Fact]
    public async Task Types_from_per_tree_analyzer_config_suppress_cross_project_references()
    {
        var source = Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}";

        var suppressed = await AnalyzeAsync(source, treeConfig: new Dictionary<string, string>
        {
            ["cairn_additional_configured_types"] = "OrderDto",
        });

        // A section-scoped .editorconfig value surfaces through per-tree options, not GlobalOptions.
        Assert.Empty(suppressed);
    }

    [Fact]
    public async Task The_same_type_at_the_same_location_is_reported_once()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Microsoft.AspNetCore.Http.HttpResults { public class Results<T1, T2> { } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
        => app.MapGet(""/orders"", () => default(Microsoft.AspNetCore.Http.HttpResults.Results<OrderDto, OrderDto>)).WithLinks();
}");

        // Both type arguments of the result union unwrap to OrderDto at the same WithLinks location; the
        // report must be deduplicated rather than doubled.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task An_ambiguous_with_links_is_ignored()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Cairn.AspNetCore { public static class MoreExtensions { public static T WithLinks<T>(this T builder) => builder; } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}");

        // Two applicable WithLinks extensions in the same namespace make the call ambiguous (no symbol,
        // two candidates) — the analyzer cannot know which one was meant, so it stays quiet.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_overloaded_method_group_handler_resolves_through_its_candidates()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class Handlers
{
    private static OrderDto Secret() => new OrderDto(1);
}
class App
{
    static OrderDto Handler() => new OrderDto(1);
    static OrderDto Handler(int id) => new OrderDto(id);
    void Endpoints(object app)
    {
        app.MapGet(""/orders"", Handler).WithLinks();
        // An inaccessible method group has no bound symbol at all — only a candidate.
        app.MapGet(""/secret"", Handlers.Secret).WithLinks();
    }
}");

        // Overloaded and unbindable method groups resolve through their candidates' return types.
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Contains("OrderDto", d.GetMessage()));
    }

    [Fact]
    public async Task A_method_group_bound_to_a_concrete_delegate_type_resolves_directly()
    {
        // Not built on the shared stubs: this MapGet takes a concrete Func<T>, so the method-group
        // conversion selects one method and the argument binds a symbol outright (no candidate fallback).
        var diagnostics = await AnalyzeAsync(@"
using Cairn.AspNetCore;
namespace Cairn { public abstract class LinkConfig<T> { } }
namespace Cairn.AspNetCore
{
    public static class Endpoints
    {
        public static object MapGet<T>(this object app, string pattern, System.Func<T> handler) => app;
        public static T WithLinks<T>(this T builder) => builder;
    }
}
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    static OrderDto Handler() => new OrderDto(1);
    void Endpoints(object app) => app.MapGet(""/orders"", Handler).WithLinks();
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task An_ambiguous_method_group_handler_resolves_through_its_candidates()
    {
        // Not built on the shared stubs: the using-static directives that manufacture the ambiguity have
        // to precede the stub namespace declarations.
        var diagnostics = await AnalyzeAsync(@"
using Cairn.AspNetCore;
using static HandlerSourceA;
using static HandlerSourceB;
namespace Cairn { public abstract class LinkConfig<T> { } }
namespace Cairn.AspNetCore
{
    public static class Endpoints
    {
        public static object MapGet(this object app, string pattern, System.Delegate handler) => app;
        public static T WithLinks<T>(this T builder) => builder;
    }
}
public record OrderDto(int Id);
public record CustomerDto(int Id);
public static class HandlerSourceA { public static OrderDto Conflicting() => new OrderDto(1); }
public static class HandlerSourceB { public static OrderDto Conflicting() => new OrderDto(2); }
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/conflict"", Conflicting).WithLinks();
}");

        // Two using-static imports make 'Conflicting' ambiguous — no bound symbol, two method candidates —
        // and the first candidate's return type is what would serialize.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Sequence_names_only_count_in_the_bcl_namespaces()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace System.Linq { public interface IList<T> { } }
namespace Other { public interface IList<T> { } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
public class Wrapper<T> { }
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app)
    {
        // A System.Linq sequence unwraps like a System.Collections.Generic one...
        app.MapGet(""/linq"", () => default(System.Linq.IList<OrderDto>)).WithLinks();
        // ...a same-named interface elsewhere does not (and an interface is no candidate itself)...
        app.MapGet(""/other"", () => default(Other.IList<OrderDto>)).WithLinks();
        // ...and an unrelated generic wrapper is reported as itself, not unwrapped.
        app.MapGet(""/wrapped"", () => default(Wrapper<OrderDto>)).WithLinks();
    }
}");

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("OrderDto"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("Wrapper"));
    }

    [Fact]
    public async Task A_with_links_call_that_fails_overload_resolution_is_still_analyzed()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks(5);
}");

        // The stray argument breaks binding but leaves the single Cairn candidate — still an opt-in.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task An_instance_method_named_with_links_is_not_an_opt_in()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
public class Builder { public Builder WithLinks() => this; }
public class Holder { public System.Func<object> WithLinks; }
public class PropertyHolder { public System.Func<object> WithLinks { get; set; } }
class App
{
    static void Helper() { }
    void Endpoints(Builder builder, Holder holder, PropertyHolder propertyHolder)
    {
        builder.WithLinks();
        holder.WithLinks();
        propertyHolder.WithLinks();
        Helper();
    }
}");

        // An instance WithLinks is a builder of something else entirely; a delegate field named WithLinks
        // binds to Invoke; a plain identifier invocation (Helper) exercises the non-member-access pre-filter.
        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        Dictionary<string, string>? globalConfig = null,
        Dictionary<string, string>? treeConfig = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions([], new TestConfigProvider(globalConfig ?? [], treeConfig ?? []));
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MissingLinkConfigAnalyzer()), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == MissingLinkConfigAnalyzer.DiagnosticId)];
    }

    private sealed class TestConfigProvider(Dictionary<string, string> global, Dictionary<string, string> perTree) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestConfigOptions(global);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestConfigOptions(perTree);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new TestConfigOptions([]);
    }

    private sealed class TestConfigOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
