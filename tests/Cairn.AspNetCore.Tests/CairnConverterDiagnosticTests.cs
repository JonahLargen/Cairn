using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

// A DTO handled by a custom JsonConverter has no object contract, so the emit stage can never inject its
// hypermedia; the diagnostic must name that cause instead of blaming deferred enumeration.
public class CairnConverterDiagnosticTests
{
    [Fact]
    public async Task A_dto_with_a_custom_converter_gets_a_converter_specific_diagnostic()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new ConvOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/conv/{id:int}", (int id) => TypedResults.Ok(new ConvOrder(id))).WithName("ConvGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/conv/5");

        // The converter's output shape wins, so no _links can appear...
        Assert.DoesNotContain("_links", body, StringComparison.Ordinal);

        // ...and the diagnostic says the converter is the reason, not a deferred sequence. It is written from
        // a Response.OnCompleted callback that can land after the client has read the body, so wait for it
        // (event-driven) before asserting.
        await logs.WaitForAsync(m => m.Contains("custom JsonConverter", StringComparison.Ordinal));

        Assert.Contains(logs.Messages, m =>
            m.Contains(nameof(ConvOrder), StringComparison.Ordinal)
            && m.Contains("custom JsonConverter", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("deferred sequence (LINQ projection", StringComparison.Ordinal));
    }

    [JsonConverter(typeof(ConvOrderConverter))]
    private sealed record ConvOrder(int Id);

    private sealed class ConvOrderConverter : JsonConverter<ConvOrder>
    {
        public override ConvOrder? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ConvOrder value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", value.Id);
            writer.WriteEndObject();
        }
    }

    private sealed class ConvOrderLinks : LinkConfig<ConvOrder>
    {
        public override void Configure(ILinkBuilder<ConvOrder> builder)
            => builder.Self(o => LinkTarget.Route("ConvGetOrder", new { id = o.Id }));
    }
}
