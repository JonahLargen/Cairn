namespace Cairn.Client;

/// <summary>
/// A compact-URI definition from <c>_links.curies</c>: a documentation <see cref="Name"/> (the prefix of
/// relations like <c>acme:widget</c>) and an <see cref="Href"/> template resolving a relation's documentation.
/// </summary>
/// <param name="Name">The prefix the curie defines (e.g. <c>acme</c>).</param>
/// <param name="Href">The documentation URL, templated on <c>{rel}</c> when <paramref name="Templated"/>.</param>
/// <param name="Templated">Whether <see cref="Href"/> is an RFC 6570 URI template.</param>
public sealed record Curie(string Name, string Href, bool Templated);
