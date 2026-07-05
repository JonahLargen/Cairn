# Cairn — Action List

Originally reviewed at `main` = `aba3217` (released `v0.6.6` = `3b9e051`).
**Status re-checked at `main` = `444d13b`** — ~45 PRs past the original review, latest
release `v0.10.0`. Each item is marked **done** / **partial** / **rejected**. Waves:
#70 (release blockers, polymorphic links), #71 (server hardening/caching/HAL-FORMS),
#72 (client/analyzer/generator/testing), #92 (trimming/AOT), **#98 (this review's own
code-review findings)**, **#99/#101 (the two leftover nits)**, **#102–#110 (the entire
Features backlog)**, and **#111–#113 (HAL Explorer + precondition/formatter docs)**,
plus supply-chain PRs #73/#93/#95 and coverage PRs #94/#97/#98. **The whole original
list plus all nine Features are done; Ecosystem is now underway (HAL Explorer shipped).
Remaining open work: the rest of the Ecosystem projects and the v2 cleanup.**

> Also shipped, *not* from this list: **#100** added client-opt-in hypermedia via
> `HypermediaFormat.None` (plain `application/json` → bare resource) + an
> `application/vnd.cairn+json` media type and a `WithHypermediaFormat(...)` override.
> **#104/#109** introduced intentional binary breaks (Embed/EmbedMany return types,
> auth overloads) staged in `src/Cairn.Core/CompatibilitySuppressions.xml` — retire
> them and bump `PackageValidationBaselineVersion` on the next release.

## Before the next release

- [x] **Done** — Injection-scoping regression fixed: `CanCarryHypermedia` now
  dispatches via `HasConfiguredSubtype` (`CairnLinkInjectionModifier.cs:72,82-85`,
  `LinkConfigRegistry.cs:102`), and the emit-miss diagnostic gained a
  `ContractMissingHypermedia` branch that names the late-registration case instead
  of blaming deferred LINQ (`CairnLinkRecorder.cs:705-748`). (#70)
- [x] **Done** — `EnsureRedirectsAreVisible` now compares URIs minus the query and
  disposes the response before throwing (`LinkPolicyRedirectHandler.cs:72-98`). (#70)
- [x] **Done** — `GetCollectionAsync` restored the v0.6.6 token-only signature and
  added the `ifNoneMatch` ETag support as a side-by-side overload
  (`CairnClient.cs:121,135`) — the recommended non-breaking fix. (#70)
- [x] **Rejected** — Promoting `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt`
  was dropped: all `PublicAPI.*.txt` files and the `PublicApiAnalyzers` reference
  were removed in favor of NuGet package validation (next item). Superseded, not
  outstanding. (#70)
- [x] **Done** — `EnablePackageValidation=true` and
  `PackageValidationBaselineVersion=0.6.6` set in `src/Directory.Build.props:32-33`;
  the baseline bump is documented in `RELEASING.md:28-41`. (#70)
- [x] **Done** — Fail-fast behaviors release-noted: `UriTemplate` `FormatException`
  (`docs/articles/client.md:113`), `AddCurie` requiring `{rel}`
  (`embedded-resources.md:130`), `default(LinkRelation)` throwing
  (`link-configs.md:49`). (#70)

## Bugs — server

- [x] **Done** — One-time startup warning when `UrlStyle` is `Absolute` without
  forwarded headers or `PublicBaseUri` (`CairnStartupWarnings.cs:20-29`, registered
  as an `IHostedService`). (#71)
- [x] **Done** — `AuthorizationPolicyStartupValidator` gained the
  `ValidateAuthorizationPolicies` opt-out and wraps `GetPolicyAsync` in try/catch
  that warns and continues (`:20-23,72-84`). (#71)
- [x] **Done** — Per-culture HAL-FORMS schema cache bounded at
  `MaxCacheEntries = 1024`, rebuilding per request past the cap
  (`HalFormsSchema.cs:28,56-61`). (#71)
- [x] **Done** — `RoutePatternCache` builds the name→pattern map once, invalidated
  by `EndpointDataSource.GetChangeToken()`; the resolver now calls `patterns.Find`
  instead of scanning every endpoint. (#71)
- [x] **Done** — OutputCache/`Vary` comment corrected
  (`CairnLinkRecorder.cs:257-261`), a caching docs page was added
  (`docs/articles/caching.md`), and a warn-once fires for policy-gated links under
  `IOutputCacheFeature` (`:587-604`). (#71)
- [x] **Done** — The per-request `OnStarting` deprecation callback is skipped unless
  an endpoint carries deprecation metadata (`CairnHeadersMiddleware.cs:22-56`,
  change-token-cached scan). (#71)
- [x] **Done** — `HypermediaProblem` writes through `IProblemDetailsService`
  (`:108-116`) so `CustomizeProblemDetails` applies, and `WithLink`/`WithAction`
  accept `LinkTarget` overloads (`:50,66`). (#71)
- [x] **Done** — Late configuration fails loudly via `CairnOptions.Freeze()` /
  `ThrowIfFrozen` (`:108-119`, frozen on first resolution), and `AddFormatter`
  rejects unparseable/wildcard media types (`:186-194`). (#71)
- [x] **Done** — A sole HAL-FORMS template is keyed `default` even without
  `AsDefault()`, and a warn-once fires on runtime default-key collision
  (`CairnLinkInjectionModifier.cs:153-170,184-197`). (#71)
- [x] **Done** — No curie is emitted for an affordance renamed to `default`
  (`CairnLinkRecorder.cs:1123-1145`, `EmittedActionNames` excludes `IsDefault`). (#71)
- [x] **Done** — HAL-FORMS `regex`/ECMAScript caveat documented in code and docs
  (`HalFormsSchema.cs:135-139`, `affordances-and-forms.md:147-151`). (#71)
- [x] **Done** — `IAsyncEnumerable<T>` "no links" caveat added to the README
  when-to-use section (`README.md:266`). (#71)

## Bugs — client

- [x] **Done** — Target-less HAL-FORMS fallback expands a templated `self` link via
  `UriTemplate.Expand` so literal braces are never submitted
  (`HypermediaParser.cs:86-93`). (#72)
- [x] **Done** — Timeout-budget cancellation now surfaces as `TaskCanceledException`
  with a `TimeoutException` inner (`CairnClient.cs:652-667`). (#72)

## Bugs — tooling

- [x] **Done** — CAIRN001 extended to `LinkTarget.RouteTemplate(...)`
  (`RouteNameAnalyzer.cs:64,277`).
- [x] **Done** — CAIRN001 restructured onto `RegisterCompilationStartAction` + a
  cached route-name index, reporting from node actions so the IDE lightbulb appears
  (`RouteNameAnalyzer.cs:40-56,86`).
- [x] **Done** — Generator guards reserved method names (`Routes`, `Equals`,
  `GetHashCode`, `ToString`) with a `_` prefix and the new **CAIRN004** diagnostic
  (`RoutesGenerator.cs:38-39,26-34,447-452`).
- [x] **Done** — CAIRN001 matches arguments by parameter name, then position
  (`RouteNameAnalyzer.cs:310-335`).
- [x] **Done** — `WithName` collection filtered to extension methods, excluding
  `Cairn.LinkTarget.WithName` (`RouteNameAnalyzer.cs:159-166`).
- [x] **Done** — `MapControllerRoute`/`MapAreaControllerRoute` route names collected
  (`RouteNameAnalyzer.cs:168-180`).
- [x] **Done** — CAIRN002 extended to `[CairnLinks]` actions and variable-broken
  `.WithLinks()` chains, with a `cairn_additional_configured_types` escape hatch
  (`MissingLinkConfigAnalyzer.cs:85-102,194-228,341,358-395`).
- [x] **Done** — CAIRN002 bound to Cairn's `WithLinks`/`LinkConfig<T>` symbols in the
  `Cairn` namespace (`MissingLinkConfigAnalyzer.cs:74-76,138-142`).
- [x] **Done** — Duplicate-report set uses `SymbolEqualityComparer.Default`
  (`MissingLinkConfigAnalyzer.cs:343,413-422`).
- [x] **Done** — `helpLinkUri` added to all descriptors (CAIRN001–004).
- [x] **Rejected** — Versioning CAIRN rules in `AnalyzerReleases.Shipped.md` was
  dropped: those files were deleted and RS2008 suppressed; rule docs now live in
  `docs/articles/route-safety.md` with per-descriptor `helpLinkUri`. Same decision
  as the `PublicAPI.txt` removal. (#72)
- [x] **Done** — Route-template parsing uses brace-depth scanning so regex
  constraints with `{}` don't drop parameters (`RoutesGenerator.cs:333-347`).
- [x] **Done** — `Map` and `MapFallback` added to the generator's chain matcher
  (`RoutesGenerator.cs:234-238`).
- [x] **Done** — Base controller types walked for inherited `[Route]` prefixes
  (`RoutesGenerator.cs:126-141`).
- [x] **Done** — `RouteInfo` carries a value-equatable `LocationInfo` so CAIRN003/004
  are navigable instead of `Location.None` (`RoutesGenerator.cs:569-583,450,459`).
- [x] **Done** — Swashbuckle ≥ 10.x floor documented in the package description and
  `docs/articles/packages.md:14,66`.
- [x] **Done** — Cairn.Testing array-root support: `ParseAll`/`ReadHypermediaListAsync`,
  throw on array root in `Parse`, default `method` to `GET`, bare-string inline
  options, and link `type`/`deprecation`/`hreflang`/`profile` parsed and assertable
  (`HypermediaResponse.cs:76-106,145,244,295,217-222`).

## Packaging / repo

- [x] **Done** — Package icon added (`icon.png` + `<PackageIcon>` in
  `src/Directory.Build.props:18`, packed with an `Exists()` guard).
- [x] **Done** — `SECURITY.md`, `CONTRIBUTING.md`, `.github/dependabot.yml` (NuGet +
  actions) and `.github/ISSUE_TEMPLATE/` (bug/feature/config) all added.
- [x] **Done** — All workflow actions SHA-pinned, including `NuGet/login`
  (`release.yml:80`).
- [x] **Done** — CI collects coverage (`--collect:"XPlat Code Coverage"`,
  `ci.yml:46`) and enforces a 95% line+branch threshold (`ci.yml:53-62`, #94/#97).
- [x] **Done** — `concurrency` group added to `ci.yml:14-16`
  (`cancel-in-progress: true`).
- [x] **Done** — Strong naming skipped; decision recorded in `CONTRIBUTING.md:60-72`.
- [x] **Done** — Analyzers ship only inside Cairn.AspNetCore (documented in
  `CONTRIBUTING.md:50-52`, `docs/articles/packages.md:29`) and the three
  `<None Include>` pack items are now guarded with `Condition="Exists(...)"`
  (`src/Cairn.AspNetCore/Cairn.AspNetCore.csproj:24-26`). (#99)
- [x] **Done** — Trim/AOT analyzers enabled (`IsAotCompatible=true` for net8.0+,
  `src/Directory.Build.props:42-44`), reflection paths annotated
  (`DynamicallyAccessedMembers`, `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`),
  and the `CairnJsonContext` AOT claim reconciled in `docs/articles/aot.md`. (#92)

## Features

**The entire Features backlog shipped in PRs #102–#110** (landed on `main` above
v0.9.0). All nine below are done.

- [x] **Done** — Client pagination iterator: `CollectionResource.EnumerateItemsAsync`
  yields an `IAsyncEnumerable<Resource<TItem>>` walking the pagination relation to
  exhaustion, with exact `maxItems`/`maxPages` caps, lazy per-page fetch, templated-
  `next` expansion, and eager argument validation. Trim/AOT-clean. (#102)
- [x] **Done** — Resource-based authorization: `RequireAuthorization(policy, o => resource)`
  on `ILinkSpec<T>`/`IAffordanceSpec<T>`, evaluated via
  `IAuthorizationService.AuthorizeAsync(user, resource, policy)` and memoized per
  (resource, policy). Adds a non-breaking `ILinkAuthorizer` resource seam (see V1). (#109)
- [x] **Done** — `When()`/`RequireAuthorization()` on `Embed`/`EmbedMany`: both now
  return `IEmbedSpec<T>` (deriving `HypermediaSpec<T>`); a gated-out embed omits its
  relation from `_embedded`. Intentional binary break, recorded in
  `CompatibilitySuppressions.xml`. (#104)
- [x] **Done** — Per-request base-URI resolver `CairnOptions.ResolvePublicBaseUri`
  (`Func<HttpContext, Uri?>`) for multi-tenant hosts, and `TransformUrl` now post-
  processes **every** emitted URL — explicit `LinkTarget.Uri` hrefs and pagination
  links included, not just route links. (#108)
- [x] **Done** — `Location` and `ETag` surfaced on `ClientResult` and `ClientResult<T>`
  (`ClientResult.cs:41,50,…`), populated from response headers on success. (#103)
- [x] **Done** — HAL-FORMS `options.link` emitted server-side via the new
  `[HalFormsOptionsLink]` attribute (`Cairn.Core`), taking precedence over inline
  enum/bool derivation per the HAL-FORMS inline-XOR-link rule. (#106)
- [x] **Done** — HTTP `Link` header (RFC 8288) for the context resource's body links,
  opt-in via `CairnOptions.EmitLinkHeader`; templated/curie links and header-unsafe
  hrefs are skipped, values escaped against injection. (#105)
- [x] **Done** — OpenAPI enhancements: per-format schema variants (`allOf` layering
  `_actions`/`_templates`), typed `_embedded` schemas, `operation.deprecated`,
  `ETagMetadata` on `WithETag` (ETag header + 304), and `IEndpointMetadataProvider` on
  `HypermediaProblem` (problem+json response) (#110); plus `WithPreconditions()`
  documenting the `412`/`428` write-precondition responses (#112).
  - [ ] Only sliver left: documenting the `Deprecation`/`Sunset` **response headers**
    (the `operation.deprecated` flag is set, but the headers themselves aren't in the
    document).
- [x] **Done** — Cairn.Testing assertions: `NotHaveTemplate`, `HaveEmbedded(rel,count)`
  /`NotHaveEmbedded`, `.And` continuity across the `_embedded` boundary,
  `HttpResponseMessage.Should()` transport helpers (`HaveStatusCode`/`HaveContentType`
  /`HaveETag`), and `WithContentType` on affordance assertions. (#107)

## Ecosystem

*The frontier — HAL Explorer is now shipped; the rest are still open.*

- [x] **Done** — HAL Explorer middleware + browsable sample API: new
  `Cairn.AspNetCore.Explorer` package with `UseCairnExplorer()` (default `/explorer`,
  Development-only unless opted in), a single embedded no-CDN HTML UI that renders
  `_links`/`_embedded`/`_templates` as real forms and negotiates format from
  `CairnOptions.MediaTypes`; the `Cairn.Sample.Api` was fleshed out as the browsable
  host. Trim/AOT-clean. (#111)
- [ ] **Open** — `dotnet new cairn-api` template.
- [ ] **Open** — Siren formatter package (`application/vnd.siren+json`). Note: #113
  documented that the `IHypermediaFormatter` extension point can't express enveloping
  formats, so a real Siren package would be format-first (or needs the V3 reshaping
  seam), not an `IHypermediaFormatter`.
- [ ] **Open** — ALPS profile documents from registered `LinkConfig<T>`s.
- [ ] **Open** — Traverson-style multi-hop client sugar (`Follow("orders","next","item")`).
- [ ] **Open** — Ketting-interop docs page and a "Cairn vs GraphQL vs OData" page.
- [ ] **Open** — Opt-in query-parameter → pagination-envelope binding.
- [ ] **Open** — `Cairn.Mcp`: expose state/auth-gated affordances as MCP tools.

## v2 (breaking window)

- [ ] **Partial** — `ILinkAuthorizer` gained the resource parameter as a **non-breaking
  default-interface method** `AuthorizeAsync(object? resource, string policy, …)` in
  #109 (delegates to the caller-only overload). The clean breaking version still
  remains for the v2 window:
  - [ ] Fold the resource overload into the primary signature (drop the default-method
    seam) and add explicit `ClaimsPrincipal` to the interface.
- [ ] **Open** — `ILinkUrlResolver`: make async and pass request context (`Resolve` is
  still sync, `LinkContext.cs:17`). Note: the multi-tenant per-request motivation was
  delivered non-breakingly via `ResolvePublicBaseUri` (F4/#108); this item is now just
  the interface cleanup.
- [ ] **Open** — `IHypermediaFormatter`: allow document reshaping; decouple
  `CairnOptions.DefaultFormat` from the `HypermediaFormat` enum. (Note: #100 added a
  `None` member to that enum, so the coupling is slightly deeper now; #113 documented
  that the current property-injection formatter *can't* reshape into enveloping formats
  like Siren/JSON:API — which is exactly the limitation this item removes.)
- [ ] **Open** — Default `UrlStyle` to `PathRelative` (still `Absolute`,
  `CairnOptions.cs:47`).
- [x] **Done** — Trimming/AOT posture declared and annotated for Cairn.Core and
  Cairn.Client (`IsAotCompatible=true` + attributes, `docs/articles/aot.md`). (#92)

## New findings (code review at #97) — all resolved

The code-review pass at #97 surfaced 16 net-new issues (7 `[Med]`, the rest
`[Low]`/nit). **PR #98 fixed all of them except one nit** (~940 lines of regression
tests plus a blocking 95%-patch-coverage gate); **#101 then closed that last nit**.
Kept here as a record — every box below is checked.

### Server

- [x] **Done** — Auth validator resolves `IAuthorizationService` inside a
  `CreateScope()` (and injects `ILoggerFactory`), so scoped authorization handlers no
  longer abort startup (`AuthorizationPolicyStartupValidator.cs`). (#98)
- [x] **Done** — Catch-all route params keep their `/` separators (`EscapeCatchAll`)
  and an unbound catch-all emits an RFC 6570 reserved expansion `{+slug}`
  (`LinkGeneratorUrlResolver.cs:77-90,115-121`). (#98)
- [x] **Done** — HAL-FORMS enum default emitted through `WireValueOf` so it matches an
  option's wire form and can preselect (`HalFormsSchema.cs`). (#98)
- [x] **Done** — `RoutePatternCache` and `CairnHeadersMiddleware` use
  `ChangeToken.OnChange`, so endpoint invalidation stays live and no stale snapshot is
  republished. (#98)
- [x] **Done** — `AddCuries` allocates its list/set lazily on the first prefix hit
  (`CairnLinkRecorder.cs`). (#98)
- [x] **Done** — `HypermediaProblem` resolves the URL resolver + mode once per
  document, and a 412 from `CairnPreconditions` echoes the current `ETag`. (#98)
- [x] **Done** — The envelope is no longer mutated: the buffered items sequence is
  registered in a per-request side table (`CairnLinkStore.RecordMaterialized`) and
  substituted at serialization via a getter-wrapping contract modifier instead of
  `property.SetValue`. Bonus: init-only/get-only envelopes now keep their item links,
  and the deferred-items warning narrowed to genuinely un-interceptable properties. (#101)

### Client

- [x] **Done** — Empty/whitespace `method` on an action/template is read as `GET`
  instead of throwing from the `Affordance` ctor (`HypermediaParser.cs`). (#98)
- [x] **Done** — Multi-select (array) submissions validate each element against the
  options (`CairnClient.cs`). (#98)
- [x] **Done** — HAL-FORMS `regex` evaluated with `ECMAScript | CultureInvariant` and
  `\A…\z` anchors, matching spec-compliant validators (`CairnClient.cs`). (#98)
- [x] **Done** — Numeric `min`/`max`/`step` also apply to numeric-string values. (#98)
- [x] **Done** — ETag read from the raw header (non-RFC-7232 values survive); a weak
  validator is sent as its strong form for `If-Match` (`CairnClient.cs`). (#98)
- [x] **Done** — `UriTemplate` throws `FormatException` on malformed/zero prefix
  modifiers (`UriTemplate.cs`). (#98)
- [x] **Done** — Problem bodies cap at 1 MiB; `GetCollectionAsync` guards a null
  `itemsProperty`; parsed `Step` is validated. (#98)

### Tooling / testing / OpenAPI

- [x] **Done** — `RoutesGenerator` isolates the constraint before scanning for `=`, so
  a regex constraint containing `=` is no longer parsed as optional; generated XML
  summaries escape CR/LF. (#98)
- [x] **Done** — OpenAPI/Swagger gate the negotiated `hal+json`/`hal-forms+json` media
  types on a new discoverable `ICairnLinksMetadata` marker added by
  `WithLinks()`/`[CairnLinks]`, so opt-out endpoints aren't advertised
  (`Internal/CairnLinksMetadata.cs`, `HypermediaJsonSchemas.OptedIntoLinks`). (#98)
- [x] **Done** — CAIRN001 code fix only rewrites a single string literal, never one
  sub-literal of a concatenation (`RouteNameCodeFixProvider.cs`). (#98)
- [x] **Done** — `HypermediaSnapshot` routes array-root responses through
  `WriteResource`, so `HypermediaOnly` and `_embedded` handling apply. (#98)

## Suggested next steps

The original list, the code-review appendix, both leftover nits, **the entire Features
backlog (#102–#110), and the first Ecosystem project (HAL Explorer, #111)** are now
done. Open work is the rest of the Ecosystem projects and the v2 cleanup — neither
blocks a release.

1. **Cut the 1.0 release — this is the move.** No correctness or feature debt remains:
   AOT-annotated, 95% line+branch+patch coverage, supply-chain hardened, docs complete.
   One concrete release chore first: #104/#109 staged intentional binary breaks in
   `src/Cairn.Core/CompatibilitySuppressions.xml` — after tagging, bump
   `PackageValidationBaselineVersion` to the new version and delete the suppressions so
   the baseline tracks the real surface again (procedure is in RELEASING.md).
2. **One doc sliver, whenever convenient (not a blocker):** document the
   `Deprecation`/`Sunset` response headers in OpenAPI — the `412`/`428` half shipped in
   #112 and the rest of F8 in #110, so only the deprecation headers remain.
3. **Run the v2 breaking window as one major bump.** V1 is half-done (the resource
   parameter exists as a non-breaking default-interface method); V2–V4 (async
   `ILinkUrlResolver`, `IHypermediaFormatter` reshaping + `DefaultFormat` decoupling,
   default `UrlStyle` → `PathRelative`) are still breaking — do them together for a
   single migration. #113 already documented the formatter-reshaping gap V3 closes.
4. **Ecosystem is the remaining frontier**, and it's adoption-driven: with HAL Explorer
   (E1) shipped, a `dotnet new cairn-api` template (E2) is the next highest-leverage for
   growth; a Siren package (E3) and `Cairn.Mcp` (E8) are bigger differentiator bets
   (Siren now needs a format-first design or the V3 seam, per #113).
