using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// SubmitAsync's client-side validation corners and CreateContent's content-type handling: submissions
// that don't form a JSON object, per-field length/required failures, patterns the regex engine cannot
// evaluate, and bodies the declared content type cannot encode.
public class CairnClientSubmitEdgeTests
{
    private static readonly Affordance Update = new("update", "/update", "POST");

    [Fact]
    public async Task A_submission_that_is_not_a_json_object_is_rejected()
    {
        var (client, _) = NewRecordingClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [new AffordanceField("reason")], values: "just a string"));

        Assert.Contains("must serialize to a JSON object", exception.Message);
    }

    [Fact]
    public async Task An_empty_string_does_not_satisfy_a_required_field()
    {
        var (client, _) = NewRecordingClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [new AffordanceField("reason") { Required = true }], new { reason = "" }));

        Assert.Contains("'reason' is required", exception.Message);
    }

    [Fact]
    public async Task A_value_shorter_than_minLength_is_rejected()
    {
        var (client, _) = NewRecordingClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [new AffordanceField("reason") { MinLength = 5 }], new { reason = "abc" }));

        Assert.Contains("at least 5 characters", exception.Message);
    }

    [Fact]
    public async Task An_invalid_regex_pattern_never_invalidates_the_value()
    {
        var (client, handler) = NewRecordingClient();

        // "(" is not a valid pattern; a server mistake must not make every submission fail client-side.
        var result = await client.SubmitAsync(Update, [new AffordanceField("reason") { Regex = "(" }], new { reason = "anything" });

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.RequestUri);
    }

    [Fact]
    public async Task A_pathological_regex_times_out_instead_of_invalidating_the_value()
    {
        var (client, handler) = NewRecordingClient();

        // Catastrophic backtracking: (a+)+ against a long non-matching input exceeds the 1s evaluation
        // budget, and a timeout is treated as "cannot evaluate", not "invalid".
        var fields = new[] { new AffordanceField("reason") { Regex = "(a+)+" } };
        var result = await client.SubmitAsync(Update, fields, new { reason = new string('a', 40) + "!" });

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.RequestUri);
    }

    [Fact]
    public async Task An_unsupported_content_type_fails_loudly()
    {
        var (client, _) = NewRecordingClient();
        var affordance = new Affordance("update", "/update", "POST") { ContentType = "text/xml" };

        // Serializing the body as JSON but labeling it text/xml would mislead the server.
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => client.InvokeAsync(affordance, new { a = 1 }));

        Assert.Contains("text/xml", exception.Message);
    }

    [Fact]
    public async Task A_form_body_supplied_as_pairs_is_sent_as_is()
    {
        var (client, handler) = NewRecordingClient();
        var affordance = new Affordance("update", "/update", "POST") { ContentType = "application/x-www-form-urlencoded" };

        var pairs = new List<KeyValuePair<string, string>> { new("reason", "late"), new("severity", "3") };
        var result = await client.InvokeAsync(affordance, pairs);

        Assert.True(result.IsSuccess);
        Assert.Equal("reason=late&severity=3", handler.RequestBody);
    }

    [Fact]
    public async Task A_form_body_that_is_not_an_object_is_rejected()
    {
        var (client, _) = NewRecordingClient();
        var affordance = new Affordance("update", "/update", "POST") { ContentType = "application/x-www-form-urlencoded" };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => client.InvokeAsync(affordance, new[] { 1, 2 }));

        Assert.Contains("must be an object", exception.Message);
    }

    [Fact]
    public async Task A_nested_value_cannot_be_form_encoded()
    {
        var (client, _) = NewRecordingClient();
        var affordance = new Affordance("update", "/update", "POST") { ContentType = "application/x-www-form-urlencoded" };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => client.InvokeAsync(affordance, new { nested = new { a = 1 } }));

        Assert.Contains("'nested'", exception.Message);
    }

    private static (CairnClient Client, RecordingHandler Handler) NewRecordingClient()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (new CairnClient(http), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
