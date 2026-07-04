using FsCheck;
using FsCheck.Fluent;

namespace Cairn.Core.Tests;

/// <summary>A candidate link relation: non-empty, not all whitespace, printable ASCII plus a sprinkling of non-ASCII.</summary>
public sealed record RelationToken(string Value)
{
    public override string ToString() => $"\"{Value}\"";
}

/// <summary>A string consisting only of whitespace characters (possibly empty) — always an invalid relation or href.</summary>
public sealed record WhitespaceString(string Value)
{
    public override string ToString() => $"\"{Value.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n")}\"";
}

/// <summary>Generators for Cairn domain inputs, registered via <c>[Property(Arbitrary = ...)]</c>.</summary>
public static class PropertyTestArbitraries
{
    // Printable ASCII plus a few non-ASCII letters/symbols so case-insensitivity and hashing see
    // more than [a-z]. Surrogates are deliberately excluded; they get dedicated tests elsewhere.
    private static Gen<char> RelationChar => Gen.OneOf(
        Gen.Choose(33, 126).Select(i => (char)i),
        Gen.Elements('é', 'Ø', 'ß', 'λ', 'Д', '日', '–'));

    public static Arbitrary<RelationToken> RelationTokens()
        => (from length in Gen.Choose(1, 24)
            from chars in RelationChar.ArrayOf(length)
            select new RelationToken(new string(chars))).ToArbitrary();

    public static Arbitrary<WhitespaceString> WhitespaceStrings()
        => (from length in Gen.Choose(0, 8)
            from chars in Gen.Elements(' ', '\t', '\r', '\n', '\f', '\v').ArrayOf(length)
            select new WhitespaceString(new string(chars))).ToArbitrary();
}
