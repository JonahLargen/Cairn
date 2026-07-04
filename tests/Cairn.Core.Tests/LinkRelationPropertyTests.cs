using FsCheck.Xunit;

namespace Cairn.Core.Tests;

// Property-based tests: each [Property] runs against 100 randomized inputs per execution,
// checking invariants that must hold for *any* input rather than hand-picked examples.
[Properties(Arbitrary = [typeof(PropertyTestArbitraries)])]
public class LinkRelationPropertyTests
{
    [Property]
    public bool Equality_ignores_case_for_any_relation(RelationToken token)
    {
        var upper = new string(token.Value.Select(char.ToUpperInvariant).ToArray());
        var lower = new string(token.Value.Select(char.ToLowerInvariant).ToArray());

        return new LinkRelation(token.Value) == new LinkRelation(upper)
            && new LinkRelation(token.Value) == new LinkRelation(lower);
    }

    [Property]
    public bool Equal_relations_hash_alike(RelationToken token)
    {
        var upper = new string(token.Value.Select(char.ToUpperInvariant).ToArray());

        return new LinkRelation(token.Value).GetHashCode() == new LinkRelation(upper).GetHashCode();
    }

    [Property]
    public bool Equality_is_symmetric(RelationToken left, RelationToken right)
    {
        var a = new LinkRelation(left.Value);
        var b = new LinkRelation(right.Value);

        return a.Equals(b) == b.Equals(a);
    }

    [Property]
    public bool Original_casing_is_preserved_for_emission(RelationToken token)
    {
        var relation = new LinkRelation(token.Value);

        return relation.Value == token.Value && relation.ToString() == token.Value;
    }

    [Property]
    public bool Construction_paths_agree(RelationToken token)
    {
        LinkRelation implicitly = token.Value;

        return implicitly == LinkRelation.FromString(token.Value)
            && implicitly == new LinkRelation(token.Value);
    }

    [Property]
    public bool Dictionary_lookup_finds_a_relation_under_any_casing(RelationToken token)
    {
        var map = new Dictionary<LinkRelation, int> { [new LinkRelation(token.Value)] = 42 };
        var recased = new string(token.Value.Select(char.ToUpperInvariant).ToArray());

        return map.TryGetValue(new LinkRelation(recased), out var found) && found == 42;
    }

    [Property]
    public bool Whitespace_only_input_is_always_rejected(WhitespaceString whitespace)
    {
        try
        {
            _ = new LinkRelation(whitespace.Value);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Property]
    public bool A_link_preserves_relation_and_href_verbatim(RelationToken relation, RelationToken href)
    {
        var link = new Link(relation.Value, href.Value);

        return link.Relation.Value == relation.Value && link.Href == href.Value && !link.Templated;
    }

    [Property]
    public bool A_link_rejects_a_whitespace_href(RelationToken relation, WhitespaceString href)
    {
        try
        {
            _ = new Link(relation.Value, href.Value);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
