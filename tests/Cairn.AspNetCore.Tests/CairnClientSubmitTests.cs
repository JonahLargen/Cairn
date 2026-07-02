using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// SubmitAsync submits a parsed HAL-FORMS template: values are validated client-side against the template's
// field metadata before anything is sent, then posted with the template's method and declared content type.
public class CairnClientSubmitTests
{
    private const string Doc = """
        {
          "name": "doc",
          "_templates": {
            "update": {
              "method": "POST",
              "target": "/update",
              "contentType": "application/x-www-form-urlencoded",
              "properties": [
                { "name": "reason", "required": true, "regex": "[a-z ]+", "maxLength": 10 },
                { "name": "severity", "type": "number", "min": 1, "max": 5 },
                { "name": "status", "options": { "inline": ["open", "closed"] } },
                { "name": "id", "readOnly": true }
              ]
            },
            "note": {
              "method": "PUT",
              "target": "/note",
              "properties": [ { "name": "text", "required": true } ]
            },
            "annotate": {
              "method": "PUT",
              "target": "/note",
              "contentType": "application/json; charset=utf-8",
              "properties": [ { "name": "text", "required": true } ]
            }
          }
        }
        """;

    [Fact]
    public async Task Submits_form_encoded_values_with_the_templates_method_and_content_type()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();
        var result = await doc.SubmitAsync<SubmitEcho>("update", new { reason = "late", severity = 3, status = "open" });

        Assert.True(result.IsSuccess);
        var echo = result.Resource!.RequireValue();
        Assert.Equal("application/x-www-form-urlencoded", echo.ContentType);
        Assert.Equal("POST", echo.Method);
        Assert.Equal("late", echo.Reason);
        Assert.Equal("3", echo.Severity);
    }

    [Fact]
    public async Task Submits_json_by_default_with_the_templates_method()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();
        var result = await doc.SubmitAsync<SubmitEcho>("note", new { text = "hello" });

        Assert.True(result.IsSuccess);
        var echo = result.Resource!.RequireValue();
        Assert.Equal("application/json", echo.ContentType);
        Assert.Equal("PUT", echo.Method);
        Assert.Equal("hello", echo.Reason);
    }

    [Fact]
    public async Task A_parameterized_json_content_type_is_submitted_as_json()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();

        // "application/json; charset=utf-8" is still JSON: the parsed media type decides, not the raw string.
        var result = await doc.SubmitAsync<SubmitEcho>("annotate", new { text = "hello" });

        Assert.True(result.IsSuccess);
        var echo = result.Resource!.RequireValue();
        Assert.Equal("application/json", echo.ContentType);
        Assert.Equal("PUT", echo.Method);
        Assert.Equal("hello", echo.Reason);
    }

    [Fact]
    public async Task A_missing_required_value_fails_validation_before_anything_is_sent()
    {
        await using var app = await StartAsync();
        var updates = app.Services.GetRequiredService<UpdateCounter>();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => doc.SubmitAsync("update", new { severity = 3 }));

        Assert.Contains("'reason' is required", exception.Message);
        Assert.Equal(0, updates.Count);   // validation rejected the submission client-side
    }

    [Fact]
    public async Task Field_constraints_are_validated_and_violations_aggregated()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => doc.SubmitAsync("update", new { reason = "THIS IS FAR TOO LOUD", severity = 9, status = "weird", id = 4 }));

        Assert.Contains("'reason' must match the pattern", exception.Message);
        Assert.Contains("'reason' must be at most 10 characters", exception.Message);
        Assert.Contains("'severity' must be at most 5", exception.Message);
        Assert.Contains("'status' must be one of: open, closed", exception.Message);
        Assert.Contains("'id' is read-only", exception.Message);
    }

    [Fact]
    public async Task A_value_below_the_minimum_is_rejected()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => doc.SubmitAsync("update", new { reason = "late", severity = 0 }));

        Assert.Contains("'severity' must be at least 1", exception.Message);
    }

    [Fact]
    public async Task Submitting_an_unknown_affordance_throws()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var doc = (await client.GetAsync<SubmitDoc>("/doc")).EnsureSuccess();

        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SubmitAsync("nope"));
    }

    [Fact]
    public async Task The_client_level_submit_validates_against_the_supplied_fields()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var affordance = new Affordance("note", "/note", "PUT");
        var fields = new[] { new AffordanceField("text") { Required = true } };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitAsync(affordance, fields));

        var result = await client.SubmitAsync(affordance, fields, new { text = "hi" });
        Assert.True(result.IsSuccess);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<UpdateCounter>();

        var app = builder.Build();

        app.MapGet("/doc", () => Results.Text(Doc, "application/prs.hal-forms+json"));

        app.MapPost("/update", async (HttpRequest request, UpdateCounter counter) =>
        {
            counter.Count++;
            var form = await request.ReadFormAsync();
            return Results.Json(new SubmitEcho(
                form["reason"].ToString(),
                form["severity"].ToString(),
                request.ContentType?.Split(';')[0] ?? "",
                request.Method));
        });

        app.MapPut("/note", async (HttpRequest request) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(request.Body);
            return Results.Json(new SubmitEcho(
                body.GetProperty("text").GetString() ?? "",
                "",
                request.ContentType?.Split(';')[0] ?? "",
                request.Method));
        });

        await app.StartAsync();
        return app;
    }

    private sealed class UpdateCounter
    {
        public int Count { get; set; }
    }

    private sealed record SubmitDoc(string Name);

    private sealed record SubmitEcho(string Reason, string Severity, string ContentType, string Method);
}
