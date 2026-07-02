using System.Collections.Immutable;
using Cairn.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class MissingLinkConfigAnalyzerTests
{
    private const string Stubs = @"
namespace Cairn { public abstract class LinkConfig<T> { } }
public static class Endpoints
{
    public static object MapGet(this object app, string pattern, System.Delegate handler) => app;
    public static object MapGroup(this object app, string prefix) => app;
    public static T WithName<T>(this T builder, string name) => builder;
    public static T WithLinks<T>(this T builder) => builder;
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

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MissingLinkConfigAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == MissingLinkConfigAnalyzer.DiagnosticId)];
    }
}
