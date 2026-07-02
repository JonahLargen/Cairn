# Changelog

## Unreleased

### Breaking changes

- **`LinkRelation` equality is now case-insensitive** (per RFC 8288, link relation types compare
  case-insensitively). Case-variant relations such as `Self` and `self` now compare equal, hash
  identically, and therefore merge into a single wire key (the first-declared casing is emitted).
  Code using `Dictionary<LinkRelation, ...>` or `HashSet<LinkRelation>` sees the new semantics:
  entries that differed only by case now collide.
- **The generated `Cairn.Routes` catalog is now `internal`** (it was `public`). A route catalog is
  app-internal by nature; emitting it as `internal` prevents duplicate-type errors against a
  hand-written `Cairn.Routes` in a referencing project and CS0433 ambiguity when several assemblies
  each generate the catalog. The class remains `partial`, so it can still be extended within the
  same assembly. If another assembly consumed a project's generated catalog, expose it explicitly
  (e.g. via a hand-written public wrapper) or use `InternalsVisibleTo`.

### Fixed

- `WithDeprecation(...)` now emits its `Deprecation`/`Sunset`/`Link` headers for MVC controller
  endpoints (including `MapControllers()` groups), not just minimal-API handlers. The headers are
  declared as endpoint metadata and emitted by a middleware `AddCairn` auto-registers.
- The OpenAPI/Swagger documents no longer advertise `application/hal+json` /
  `application/prs.hal-forms+json` for bare collection responses (the wire keeps them
  `application/json`), and now document pagination envelopes (`PagedResource<T>`, `CursorPage<T>`)
  with their negotiable media types and pagination `_links`.
- The OpenAPI/Swagger schema of a linked type no longer overwrites a `_links`/`_embedded`/
  `_actions`/`_templates` property the DTO itself declares, matching the wire's collision guard.
- `[HttpGet(..., Name = nameof(X))]` (and `const`-declared names) are now recognized by the CAIRN001
  analyzer, matching the generator, so referencing such names no longer produces a false warning.
  CAIRN001 also now binds `LinkTarget.Route(...)` receivers semantically: look-alike `LinkTarget`
  types in other namespaces are ignored, and `using static Cairn.LinkTarget` call sites are checked.
- Registering two affordances that both claim the reserved `default` HAL-FORMS template key
  (two `AsDefault()` calls, or `AsDefault()` plus an affordance named `default`) now throws at
  registration time instead of silently emitting last-wins.
- The generated route catalog maps `min`/`max`/`range` constraints to `long` (previously `string`),
  and optional (`{id:int?}`) or defaulted (`{id=5}`) route parameters generate nullable parameters
  with a `null` default that are omitted from the route values when not supplied.
- `CairnClient` accepts parameterized JSON affordance content types such as
  `application/json; charset=utf-8` (previously `NotSupportedException`).
- `CollectionResource.FollowAsync("next", null)` now throws `ArgumentNullException` explaining the
  overload trap instead of failing later with a `NullReferenceException`.
- URI template expansion: a bare `%` in `{+var}`/`{#var}` values is now percent-encoded (only valid
  pct-triplets pass through), and the prefix modifier (`{var:n}`) counts code points, so it no
  longer splits surrogate pairs into replacement characters.
- Warn-once diagnostics are now scoped per host container instead of process-wide, so side-by-side
  hosts (e.g. `WebApplicationFactory` test suites) each get their own diagnostics.

### Removed

- The unshipped `string.Hypermedia()` extension in `Cairn.Testing` (it surfaced on every string in
  IntelliSense). Use `HypermediaResponse.Parse(json)`, or the `HttpResponseMessage`/`HttpClient`
  extensions.

### Added

- `Cairn.Testing`: `HaveLinkMatching(rel, pattern)`, `WithHrefMatching(pattern)` (affordances), and
  `WithTargetMatching(pattern)` (HAL-FORMS templates) — `{param}` matches one path segment and a
  trailing `*` makes the pattern a prefix match, so assertions survive host/port differences.
