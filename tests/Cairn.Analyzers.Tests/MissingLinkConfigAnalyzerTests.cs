using System.Collections.Immutable;
using Cairn.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class MissingLinkConfigAnalyzerTests
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
        public static object MapGet(this object app, string pattern, System.Delegate handler) => app;
        public static object MapGroup(this object app, string prefix) => app;
        public static T WithName<T>(this T builder, string name) => builder;
        public static T WithLinks<T>(this T builder) => builder;
    }
}
";

    [Fact]
    public async Task Flags_a_with_links_endpoint_returning_an_unconfigured_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithName(""GetOrder"").WithLinks();
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(MissingLinkConfigAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Does_not_flag_a_configured_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
class OrderLinks : Cairn.LinkConfig<OrderDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Stays_silent_when_the_compilation_declares_no_link_configs_at_all()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}");

        // Configs likely live in another project; flagging every endpoint would be all noise.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Unwraps_tasks_and_sequences_to_the_element_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => System.Threading.Tasks.Task.FromResult(new System.Collections.Generic.List<OrderDto>())).WithLinks();
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_config_on_a_base_type_covers_derived_resources()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record BaseDto(int Id);
public record SpecialDto(int Id) : BaseDto(Id);
class BaseLinks : Cairn.LinkConfig<BaseDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/special"", () => new SpecialDto(1)).WithLinks();
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_group_level_with_links_is_out_of_scope()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGroup(""/orders"").WithLinks();
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Follows_a_chain_broken_through_a_variable()
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
        endpoint.WithLinks();
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Flags_a_cairn_links_controller_action_returning_an_unconfigured_type()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
public class OrdersController
{
    [Cairn.AspNetCore.CairnLinks]
    public OrderDto Get(int id) => new OrderDto(id);
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_class_level_cairn_links_covers_every_action()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
[Cairn.AspNetCore.CairnLinks]
public class OrdersController
{
    public OrderDto Get(int id) => new OrderDto(id);
    public CustomerDto Customer(int id) => new CustomerDto(id);
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Ignores_a_with_links_extension_from_another_library()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Other { public static class OtherExtensions { public static T WithLinks<T>(this T builder) => builder; } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => Other.OtherExtensions.WithLinks(app.MapGet(""/orders"", () => new OrderDto(1)));
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_look_alike_link_config_from_another_namespace_does_not_configure()
    {
        var diagnostics = await AnalyzeAsync(Stubs + @"
namespace Other { public abstract class LinkConfig<T> { } }
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class OrderLinks : Other.LinkConfig<OrderDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("OrderDto", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Types_from_analyzer_config_suppress_cross_project_references()
    {
        var source = Stubs + @"
public record OrderDto(int Id);
public record CustomerDto(int Id);
class CustomerLinks : Cairn.LinkConfig<CustomerDto> { }
class App
{
    void Endpoints(object app) => app.MapGet(""/orders"", () => new OrderDto(1)).WithLinks();
}";

        var flagged = await AnalyzeAsync(source);
        Assert.Single(flagged);

        var suppressed = await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["cairn_additional_configured_types"] = "OrderDto, AnotherDto",
        });
        Assert.Empty(suppressed);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, Dictionary<string, string>? globalConfig = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions([], new TestConfigProvider(globalConfig ?? []));
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MissingLinkConfigAnalyzer()), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == MissingLinkConfigAnalyzer.DiagnosticId)];
    }

    private sealed class TestConfigProvider(Dictionary<string, string> global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestConfigOptions(global);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestConfigOptions([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new TestConfigOptions([]);
    }

    private sealed class TestConfigOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
