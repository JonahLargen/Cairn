namespace Cairn.Core.Tests;

public class LinkRelationCaseTests
{
    [Fact]
    public void Relations_compare_case_insensitively_per_rfc_8288()
    {
        Assert.Equal(new LinkRelation("Self"), new LinkRelation("self"));
        Assert.True(new LinkRelation("NEXT") == new LinkRelation("next"));
        Assert.False(new LinkRelation("self") != new LinkRelation("SELF"));
        Assert.NotEqual(new LinkRelation("self"), new LinkRelation("next"));
    }

    [Fact]
    public void Hash_codes_agree_for_relations_differing_only_in_case()
    {
        Assert.Equal(new LinkRelation("Acme:Widget").GetHashCode(), new LinkRelation("acme:widget").GetHashCode());
    }

    [Fact]
    public void Original_casing_is_preserved_for_emission()
    {
        Assert.Equal("DescribedBy", new LinkRelation("DescribedBy").Value);
        Assert.Equal("DescribedBy", new LinkRelation("DescribedBy").ToString());
    }

    [Fact]
    public void Dictionary_lookup_keyed_by_relation_is_case_insensitive()
    {
        var map = new Dictionary<LinkRelation, string> { [new LinkRelation("edit-form")] = "x" };

        Assert.True(map.ContainsKey(new LinkRelation("Edit-Form")));
    }

    [Fact]
    public void A_default_relation_equals_itself_and_no_named_relation()
    {
        Assert.Equal(default, default(LinkRelation));
        Assert.NotEqual(new LinkRelation("self"), default);
        Assert.Equal(0, default(LinkRelation).GetHashCode());
    }
}
