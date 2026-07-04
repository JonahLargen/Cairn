using System.Collections;
using System.Net;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// RFC 6570 branches beyond the edge cases already covered: the op-reserve operators, malformed prefix
// modifiers, explicit-null and boolean variables, maps that follow a scalar in the same expression,
// percent-triplet detection at the end of a reserved value, and the reflective pair/dictionary paths
// a hostile or exotic variables object can reach.
public class UriTemplateBranchTests
{
    [Theory]
    [InlineData("=")]
    [InlineData(",")]
    [InlineData("!")]
    [InlineData("@")]
    [InlineData("|")]
    public async Task Every_op_reserve_operator_is_a_processing_error(string op)
    {
        var (client, _) = NewRecordingClient();

        // RFC 6570 §2.2 reserves these operators for future extensions; expanding them as literal
        // variable names would silently request the wrong resource.
        var thrown = await Assert.ThrowsAsync<FormatException>(
            () => client.FollowAsync<JsonElement>(new Link("item", $"/items{{{op}x}}", templated: true), new { x = 1 }));

        Assert.Contains("op-reserve", thrown.Message);
    }

    [Fact]
    public async Task A_non_numeric_prefix_modifier_expands_the_whole_value()
    {
        var (client, handler) = NewRecordingClient();

        // ":zz" is not a valid max-length, so no prefix applies and the full value expands.
        await client.FollowAsync<JsonElement>(new Link("item", "/items/{v:zz}", templated: true), new { v = "abcdef" });

        Assert.Equal("/items/abcdef", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_prefix_longer_than_the_value_expands_the_whole_value()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items/{v:9}", templated: true), new { v = "abc" });

        Assert.Equal("/items/abc", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_prefix_shorter_than_the_value_truncates_it()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items/{v:2}", templated: true), new { v = "abcdef" });

        Assert.Equal("/items/ab", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_explicitly_null_variable_is_treated_as_undefined()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new Dictionary<string, object?> { ["page"] = null, ["size"] = 5 };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?page,size}", templated: true), variables);

        Assert.Equal("?size=5", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Boolean_variables_expand_as_lowercase_json_booleans()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("search", "/s{?off,on}", templated: true), new { off = false, on = true });

        Assert.Equal("?off=false&on=true", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task A_map_following_a_scalar_continues_with_the_separator()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new { q = "term", filter = new Dictionary<string, string> { ["a"] = "1" } };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?q,filter*}", templated: true), variables);

        // The map is not the first defined variable, so it joins with '&' rather than opening with '?'.
        Assert.Equal("?q=term&a=1", handler.RequestUri!.Query);
    }

    [Theory]
    [InlineData("50%", "50%25")]                // '%' at the very end cannot start a pct-triplet
    [InlineData("%A", "%25A")]                  // one trailing hex digit is not a triplet either
    [InlineData("%2G", "%252G")]                // second digit is not hex
    [InlineData("%G2", "%25G2")]                // first digit is not hex
    [InlineData("a%2Fb", "a%2Fb")]              // a valid pct-triplet passes through untouched
    public async Task Reserved_expansion_only_keeps_a_percent_that_starts_a_valid_triplet(string value, string expected)
    {
        var resource = await GetCurieResourceAsync();

        Assert.Equal($"/docs/{expected}", resource.DocumentationFor($"x:{value}"));
    }

    [Fact]
    public async Task Reserved_expansion_keeps_every_reserved_and_unreserved_character()
    {
        var resource = await GetCurieResourceAsync();

        // Unreserved and reserved (gen-delims/sub-delims) characters survive reserved expansion; anything
        // else — including the ASCII neighbours just outside the alphanumeric ranges — percent-encodes.
        var value = "AZaz09-._~" + ":/?#[]@!$&'()*+,;=" + " \"<>\\^`{|}";
        var expected = "AZaz09-._~" + ":/?#[]@!$&'()*+,;=" + "%20%22%3C%3E%5C%5E%60%7B%7C%7D";

        Assert.Equal($"/docs/{expected}", resource.DocumentationFor($"x:{value}"));
    }

    [Fact]
    public async Task A_pair_sequence_element_without_usable_key_or_value_is_skipped()
    {
        var (client, handler) = NewRecordingClient();

        // The sequence classifies as a map via IEnumerable<KeyValuePair<string,·>>, but its non-generic
        // enumeration yields elements the reflective Key/Value read cannot use; only the real pair expands.
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?m*}", templated: true), new { m = new OddPairSequence() });

        Assert.Equal("?real=1", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task A_legacy_dictionary_entry_with_a_null_key_is_skipped()
    {
        var (client, handler) = NewRecordingClient();

        // A custom IDictionary (unlike Hashtable) can hand back a DictionaryEntry whose key is null;
        // it must be dropped rather than expand as the text "null" or throw.
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?m*}", templated: true), new { m = new NullKeyDictionary() });

        Assert.Equal("?b=2", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Write_only_and_indexer_properties_do_not_supply_variables()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("search", "/s{?Page,WriteOnly}", templated: true), new TrickyVariables());

        // Only the readable, non-indexed property expands; the others cannot be read as variables.
        Assert.Equal("?Page=2", handler.RequestUri!.Query);
    }

    private sealed class TrickyVariables
    {
        public string Page => "2";

        public string WriteOnly
        {
            set => _ = value;
        }

        public string this[int index] => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Classified as a map (IEnumerable<KeyValuePair<string, string>>), but the non-generic enumeration
    // yields a null, an element without a Key property, one with a non-string Key, one without a Value
    // property, and finally a real pair.
    private sealed class OddPairSequence : IEnumerable<KeyValuePair<string, string>>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => throw new NotSupportedException("Expansion enumerates the non-generic view.");

        IEnumerator IEnumerable.GetEnumerator()
        {
            object?[] elements = [null, new NoKey(), new IntKey(), new KeyOnly("half"), KeyValuePair.Create("real", "1")];
            return elements.GetEnumerator();
        }

        private sealed class NoKey;

        private sealed class IntKey
        {
            public int Key { get; } = 1;
        }

        private sealed class KeyOnly(string key)
        {
            public string Key { get; } = key;
        }
    }

    // The minimal IDictionary that can enumerate a null-keyed DictionaryEntry (BCL dictionaries reject them).
    private sealed class NullKeyDictionary : IDictionary
    {
        private readonly DictionaryEntry[] _entries = [new DictionaryEntry(null!, "skipped"), new DictionaryEntry("b", "2")];

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public int Count => _entries.Length;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public ICollection Keys => throw new NotSupportedException();

        public ICollection Values => throw new NotSupportedException();

        public object? this[object key]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public IDictionaryEnumerator GetEnumerator() => new Enumerator(_entries);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Add(object key, object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object key) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();

        public void Remove(object key) => throw new NotSupportedException();

        private sealed class Enumerator(DictionaryEntry[] entries) : IDictionaryEnumerator
        {
            private int _index = -1;

            public DictionaryEntry Entry => entries[_index];

            public object Key => entries[_index].Key;

            public object? Value => entries[_index].Value;

            public object Current => entries[_index];

            public bool MoveNext() => ++_index < entries.Length;

            public void Reset() => _index = -1;
        }
    }

    // A curie with a reserved-expansion template returns the expanded string directly (no Uri
    // normalization in between), so encoding-sensitive assertions can be made verbatim.
    private static async Task<Resource<JsonElement>> GetCurieResourceAsync()
    {
        const string body = """
            {
              "_links": {
                "self": { "href": "/things/1" },
                "curies": [{ "name": "x", "href": "/docs/{+rel}", "templated": true }]
              }
            }
            """;
        var http = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://localhost") };
        return (await new CairnClient(http).GetAsync<JsonElement>("/things/1")).EnsureSuccess();
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/hal+json"),
            });
    }
}
