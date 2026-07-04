using System.Net;
using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Client-side fixes from the code review: multi-select and numeric-string validation, ECMAScript-anchored
// HAL-FORMS regex, step validation, an empty method defaulting to GET, non-RFC-7232 ETag preservation, weak
// If-Match strengthening, and the GetCollectionAsync null-guard.
public class CairnClientReviewFixTests
{
    private static readonly Affordance Update = new("update", "/update", "POST");

    [Fact]
    public async Task Each_element_of_a_multi_select_submission_is_validated_against_the_options()
    {
        var (client, handler) = NewRecordingClient();
        var field = new AffordanceField("tags")
        {
            Options = [new AffordanceFieldOption("red"), new AffordanceFieldOption("green"), new AffordanceFieldOption("blue")],
        };

        // A valid array of options passes — the whole array's raw text is never compared to a single option.
        var ok = await client.SubmitAsync(Update, [field], new { tags = new[] { "red", "blue" } });
        Assert.True(ok.IsSuccess);
        Assert.NotNull(handler.RequestUri);

        // An element outside the options is rejected on its own.
        var bad = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [field], new { tags = new[] { "red", "purple" } }));
        Assert.Contains("must be one of", bad.Message);
    }

    [Fact]
    public async Task A_numeric_value_submitted_as_a_string_is_still_range_checked()
    {
        var (client, handler) = NewRecordingClient();
        var field = new AffordanceField("qty") { Max = 100 };

        // "150" is a JSON string, but the range check must still see 150 and reject it against Max 100.
        var over = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [field], new { qty = "150" }));
        Assert.Contains("at most 100", over.Message);

        // A numeric string within range passes.
        var ok = await client.SubmitAsync(Update, [field], new { qty = "50" });
        Assert.True(ok.IsSuccess);
        Assert.NotNull(handler.RequestUri);
    }

    [Fact]
    public async Task Hal_forms_regex_uses_ecmascript_semantics_and_absolute_anchors()
    {
        var (client, _) = NewRecordingClient();
        var digits = new AffordanceField("code") { Regex = "\\d+" };

        // ECMAScript \d is ASCII-only, so an Arabic-Indic digit a spec-compliant validator rejects is rejected.
        var unicodeDigit = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [digits], new { code = "٥" }));
        Assert.Contains("must match the pattern", unicodeDigit.Message);

        // \z (not $) anchors to the absolute end, so a trailing newline no longer sneaks a value past.
        var trailingNewline = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [new AffordanceField("word") { Regex = "abc" }], new { word = "abc\n" }));
        Assert.Contains("must match the pattern", trailingNewline.Message);

        // A plainly matching ASCII value still passes.
        var ok = await client.SubmitAsync(Update, [digits], new { code = "123" });
        Assert.True(ok.IsSuccess);
    }

    [Fact]
    public async Task A_value_that_is_not_a_multiple_of_step_is_rejected()
    {
        var (client, handler) = NewRecordingClient();
        var field = new AffordanceField("qty") { Min = 1, Step = 5 };

        // 12 is not 1 + k*5, so it mismatches the step.
        var mismatch = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [field], new { qty = 12 }));
        Assert.Contains("multiple of 5", mismatch.Message);

        // 6 == 1 + 1*5 is a valid step from the base.
        var ok = await client.SubmitAsync(Update, [field], new { qty = 6 });
        Assert.True(ok.IsSuccess);
        Assert.NotNull(handler.RequestUri);
    }

    [Fact]
    public async Task A_zero_or_absent_step_imposes_no_step_check()
    {
        var (client, _) = NewRecordingClient();

        // A non-positive step is ignored (no granularity constraint), so any value passes the step rule.
        var zero = await client.SubmitAsync(Update, [new AffordanceField("qty") { Step = 0 }], new { qty = 7 });
        Assert.True(zero.IsSuccess);
    }

    [Fact]
    public async Task A_step_mismatch_without_a_min_reports_from_zero_alongside_other_errors()
    {
        var (client, _) = NewRecordingClient();

        // No Min, so the step base is 0 and the message omits "starting from"; the Max violation also fires,
        // so the step error appends to an already-started error list rather than starting a fresh one.
        var field = new AffordanceField("qty") { Max = 10, Step = 3 };
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(Update, [field], new { qty = 11 }));

        Assert.Contains("at most 10", error.Message);
        Assert.Contains("must be a multiple of 3.", error.Message);
    }

    [Fact]
    public async Task A_whitespace_only_etag_is_treated_as_absent()
    {
        var handler = new StubHandler("{}", response => response.Headers.TryAddWithoutValidation("ETag", "   "));
        var client = new CairnClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

        // A header that is present but blank carries no validator, so the surfaced ETag is null.
        var resource = (await client.GetAsync<System.Text.Json.JsonElement>("/things/1")).EnsureSuccess();
        Assert.Null(resource.ETag);
    }

    [Fact]
    public async Task An_action_with_an_empty_method_is_read_as_get()
    {
        const string body = """
            {
              "_links": { "self": { "href": "/things/1" } },
              "_actions": { "refresh": { "href": "/things/1", "method": "  " } }
            }
            """;
        var client = new CairnClient(new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://localhost") });

        // An empty/whitespace method would throw from the Affordance constructor; parsing must never throw,
        // so it defaults to GET instead.
        var resource = (await client.GetAsync<System.Text.Json.JsonElement>("/things/1")).EnsureSuccess();
        Assert.Equal("GET", resource.Affordances["refresh"].Method);
    }

    [Fact]
    public async Task A_non_rfc7232_etag_is_preserved_instead_of_dropped()
    {
        // A bare token ETag is not RFC 7232 (unquoted), so the typed Headers.ETag parser returns null; the
        // raw header value must still round-trip as an opaque validator.
        var handler = new StubHandler("{}", response => response.Headers.TryAddWithoutValidation("ETag", "raw-token"));
        var client = new CairnClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

        var resource = (await client.GetAsync<System.Text.Json.JsonElement>("/things/1")).EnsureSuccess();
        Assert.Equal("raw-token", resource.ETag);
    }

    [Fact]
    public async Task A_weak_validator_is_sent_as_a_strong_if_match()
    {
        var (client, handler) = NewRecordingClient();

        // RFC 9110 §13.1.1 compares If-Match strongly, so a weak W/"..." validator must not be echoed there;
        // the client sends its strong form.
        await client.InvokeAsync(Update, body: new { a = 1 }, ifMatch: "W/\"v9\"");

        Assert.Equal("\"v9\"", handler.IfMatch);
    }

    [Fact]
    public async Task GetCollectionAsync_rejects_a_null_items_property()
    {
        var (client, _) = NewRecordingClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.GetCollectionAsync<System.Text.Json.JsonElement>("/things", itemsProperty: null!, ifNoneMatch: null));
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

        public string? IfMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            IfMatch = request.Headers.TryGetValues("If-Match", out var values) ? string.Concat(values) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHandler(string body, Action<HttpResponseMessage>? configure = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/hal+json"),
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        }
    }
}
