# Cairn — Action List

Originally reviewed at `main` = `aba3217` (released `v0.6.6` = `3b9e051`).
**Status re-checked at `main` = `d9cdc7a` (PR #97)** — the tree is ~29 PRs past the
original review. Each item is marked **done** / **partial** / **rejected**. Fix
waves landed in PRs #70 (release blockers, polymorphic links), #71 (server
hardening/caching/HAL-FORMS), #72 (client/analyzer/generator/testing), #92
(trimming/AOT), plus supply-chain PRs #73/#93/#95 and coverage PRs #94/#97.

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
- [ ] **Partial** — Analyzers-ship-only-inside-Cairn.AspNetCore is documented
  (`CONTRIBUTING.md:50-52`, `docs/articles/packages.md:29`), but the pack items are
  not yet guarded.
  - [ ] Add `Condition="Exists(...)"` to the `Cairn.Analyzers.dll` /
    `Cairn.CodeFixes.dll` / `Cairn.SourceGenerators.dll` `<None Include>` pack items
    (`src/Cairn.AspNetCore/Cairn.AspNetCore.csproj:24-26`).
- [x] **Done** — Trim/AOT analyzers enabled (`IsAotCompatible=true` for net8.0+,
  `src/Directory.Build.props:42-44`), reflection paths annotated
  (`DynamicallyAccessedMembers`, `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`),
  and the `CairnJsonContext` AOT claim reconciled in `docs/articles/aot.md`. (#92)

## Features

*None of the enhancement items below have been implemented — this is the remaining
value-add backlog. (`V5` of the AOT work is the one exception, marked in v2.)*

- [ ] **Open** — Client pagination iterator: `IAsyncEnumerable<Resource<TItem>>`
  walking `next` to exhaustion, with page/item caps. (Only manual single-hop
  `FollowAsync("next")` exists today, `CollectionResource.cs:55-78`.)
- [ ] **Open** — Resource-based authorization overload evaluating
  `AuthorizeAsync(user, resource, policy)` (needs the v2 `ILinkAuthorizer` seam).
- [ ] **Open** — `When()`/`RequireAuthorization()` on `Embed`/`EmbedMany` (they
  currently return `void`, `LinkConfig.cs:57,65`).
- [ ] **Open** — Per-request base-URI resolver (`Func<HttpContext, Uri>`) for
  multi-tenant hosts; also apply `TransformUrl` to pagination links and explicit
  hrefs (today it touches only route links).
- [ ] **Open** — Surface `Location` and `ETag` on `ClientResult` (`ClientResult.cs`).
- [ ] **Open** — HAL-FORMS `options.link` (options by reference) on the server (the
  client can already parse it).
- [ ] **Open** — Emit the HTTP `Link` header (RFC 8288) for body links (only the
  deprecation link is emitted today).
- [ ] **Open** — OpenAPI enhancements (all unbuilt): set `operation.Deprecated`;
  `ETagMetadata` on `WithETag`; `IEndpointMetadataProvider` on `HypermediaProblem`;
  typed `_embedded` schemas; per-format schema variants.
- [ ] **Open** — Cairn.Testing assertions: `NotHaveTemplate`, embedded-count,
  `.And`-chain continuity from embedded assertions, status/content-type/ETag helpers,
  `WithContentType` on affordance assertions.

## Ecosystem

*All open — forward-looking ecosystem projects, none started.*

- [ ] **Open** — HAL Explorer middleware + browsable sample API.
- [ ] **Open** — `dotnet new cairn-api` template.
- [ ] **Open** — Siren formatter package (`application/vnd.siren+json`).
- [ ] **Open** — ALPS profile documents from registered `LinkConfig<T>`s.
- [ ] **Open** — Traverson-style multi-hop client sugar (`Follow("orders","next","item")`).
- [ ] **Open** — Ketting-interop docs page and a "Cairn vs GraphQL vs OData" page.
- [ ] **Open** — Opt-in query-parameter → pagination-envelope binding.
- [ ] **Open** — `Cairn.Mcp`: expose state/auth-gated affordances as MCP tools.

## v2 (breaking window)

- [ ] **Open** — `ILinkAuthorizer`: add `ClaimsPrincipal` and a resource parameter.
- [ ] **Open** — `ILinkUrlResolver`: make async and pass request context.
- [ ] **Open** — `IHypermediaFormatter`: allow document reshaping; decouple
  `CairnOptions.DefaultFormat` from the `HypermediaFormat` enum.
- [ ] **Open** — Default `UrlStyle` to `PathRelative` (still `Absolute`,
  `CairnOptions.cs:42`).
- [x] **Done** — Trimming/AOT posture declared and annotated for Cairn.Core and
  Cairn.Client (`IsAotCompatible=true` + attributes, `docs/articles/aot.md`). (#92)

## New findings (code review at #97)

Net-new issues found reviewing the current tree (not in the original list).
Severity in brackets; none are release-critical, but the `[Med]` ones are
cheap correctness fixes worth landing before v1.

### Server

- [ ] **[Med]** `AuthorizationPolicyStartupValidator` resolves `IAuthorizationService`
  from the **root** provider (it's a singleton `IHostedService`), so an app with any
  *scoped* authorization handler (e.g. a resource handler using `DbContext`) throws
  under `ValidateScopes` and aborts startup — the exact apps that use policy-gated
  Cairn links (`AuthorizationPolicyStartupValidator.cs:53,60`). Fix: resolve inside a
  `CreateScope()`, or inspect the `ServiceDescriptor` instead of instantiating.
- [ ] **[Med]** `ResolveRouteTemplate` `Uri.EscapeDataString`s every route parameter,
  so a catch-all (`{**slug}`) value like `docs/intro/setup` gets its `/` encoded to
  `%2F` (wrong URL), and an unbound catch-all emits `{slug}` (drops the `*` marker)
  (`LinkGeneratorUrlResolver.cs:73-79`). Fix: special-case `parameter.IsCatchAll`.
- [ ] **[Low]** HAL-FORMS enum default `value` uses the enum *member name* while
  `options` use the serialized wire form, so the default matches no option and can't
  preselect (`HalFormsSchema.cs:171-177` vs `:209-235`). Route the default through
  `WireValueOf`.
- [ ] **[Low]** The change-token cache pattern in `RoutePatternCache` and
  `CairnHeadersMiddleware` can republish a stale snapshot if endpoints change during
  the build window (callback fires, then the stale build overwrites the nulled field
  and the token is already spent). Use `ChangeToken.OnChange` (as
  `CairnOptionsMiddleware.cs:22` already does).
- [ ] **[Low, perf]** `AddCuries` allocates a `List`+`HashSet` and scans all rels per
  linked resource whenever *any* curie is registered, even when no rel uses a prefix —
  per-item churn on large paged responses (`CairnLinkRecorder.cs:1094-1101`). Allocate
  lazily on first prefix hit.
- [ ] **[Nit]** `HypermediaProblem.ResolveHref` re-resolves `ILinkUrlResolver` +
  `CairnOptions` per link/action; `MaterializeEnvelopeItems` writes the buffered list
  back onto a possibly-shared envelope via `SetValue`; a 412 from `CairnPreconditions`
  doesn't echo the current `ETag`.

### Client

- [ ] **[Med]** A JSON `"method": ""` (empty/whitespace) on an action/template throws
  `ArgumentException` from the `Affordance` ctor, which escapes the client's
  no-throw-on-response contract (the guard only catches `JsonException`/
  `NotSupportedException`) (`HypermediaParser.cs:73,98`). Treat empty method as `GET`.
- [ ] **[Med]** Multi-select (array) submissions to an options field always fail
  client validation — the array's raw text is compared to a single scalar option
  (`CairnClient.cs:311-320`). Validate each element individually.
- [ ] **[Med]** Client-side HAL-FORMS `regex` is evaluated with .NET semantics, so
  values a spec-compliant (ECMAScript/HTML5) validator rejects are accepted — e.g.
  `\d` matches Unicode digits, `$` matches before a trailing `\n`
  (`CairnClient.cs:325-339`). Use `RegexOptions.ECMAScript | CultureInvariant` and
  `\A…\z` anchors. (Distinct from the server-side *doc* item, which is done.)
- [ ] **[Low]** Numeric `min`/`max` are skipped when a value is submitted as a JSON
  string (range checks gate on `JsonValueKind.Number`), so `"150"` passes `Max:100`
  (`CairnClient.cs:298-309`).
- [ ] **[Low]** ETag is dropped when the server sends a non-RFC-7232 value (typed
  `Headers.ETag` returns null), and a weak `W/"…"` validator is echoed back as
  `If-Match` (RFC 7232 forbids weak comparison there) (`CairnClient.cs:498,506,538`).
- [ ] **[Low]** `UriTemplate` silently tolerates malformed/zero prefix modifiers
  (`{v:}`, `{v:abc}` → full value; `{v:0}` → empty) while being strict elsewhere
  (`UriTemplate.cs:89-98`). Throw `FormatException`.
- [ ] **[Nit]** `ReadProblemAsync` buffers error bodies up to the full success cap
  (~2 GB); `SubmitAsync` never validates the parsed `Step`; `GetCollectionAsync`
  doesn't guard a null `itemsProperty` the way `CollectionResource.FollowAsync` does.

### Tooling / testing / OpenAPI

- [ ] **[Med]** A route parameter whose constraint contains `=` (e.g.
  `{pwd:regex((?=.*\d))}`) is parsed as optional/nullable — `ParseParameters` scans
  for `=` over the whole `{...}` body before isolating the constraint — so the
  generator emits a required value as omittable and links break
  (`RoutesGenerator.cs:361-372`).
- [ ] **[Med]** OpenAPI/Swagger advertise `application/hal+json` negotiation and
  `_links`/`_actions` on every endpoint returning a configured *type*, even endpoints
  that never opted in via `.WithLinks()`/`[CairnLinks]` — so a generated client asking
  for `hal+json` gets plain JSON (`HypermediaJsonSchemas.cs:78-86,133-158` and the
  operation/schema transformers). Root cause: `WithLinks()` leaves no discoverable
  endpoint metadata to gate on.
- [ ] **[Low]** The CAIRN001 code fix can rewrite the wrong sub-literal of a
  concatenated constant route name (`"Get" + "Order"` → `"GetOrders" + "Order"`)
  (`RouteNameCodeFixProvider.cs:53-59`). Only offer the fix when the span is a single
  string literal.
- [ ] **[Low]** The generated XML `<summary>` isn't newline-safe: a route name
  containing `\n` splits the `///` line into uncompilable C#
  (`RoutesGenerator.cs:474,558-559`). Escape CR/LF.
- [ ] **[Low]** `HypermediaSnapshot` ignores `HypermediaOnly` and `_embedded`
  resource handling for array-root responses (routes them through `WriteValue`, not
  `WriteResource`), so a snapshot silently keeps data properties it was told to drop
  (`HypermediaSnapshot.cs:55-61`).

## Suggested next steps

The original pre-release list is effectively **cleared** — every server, client, and
tooling bug is fixed, and the two "rejected" items were deliberate design reversals
(package validation instead of `PublicAPI.txt`; docs + `helpLinkUri` instead of
`AnalyzerReleases.md`). What remains is the new-findings backlog and the roadmap.

1. **Batch a "pre-v1 correctness" PR** (recommended first move). Fold the seven
   `[Med]` findings above plus the one open **Partial** packaging item (analyzer
   `Exists()` guards) into a single focused PR. They're all small, contained, and each
   is a correctness issue a real user would hit: client no-throw escape, multi-select
   validation, client regex semantics, generator `=`-constraint, OpenAPI over-
   advertising HAL, root-provider auth-validator crash, and catch-all URL escaping.
   Land this and the library is genuinely v1-ready.
2. **Optionally sweep the `[Low]` findings** into a second cleanup PR (or the same
   one) — none block release, but the ETag/weak-validator, change-token race, and
   enum-default items are cheap and remove sharp edges.
3. **Cut v1.0.0.** After step 1 there is no known correctness debt; the package is
   AOT-annotated, coverage-gated at 95%, supply-chain hardened, and docs are complete.
4. **Pick 2–3 post-v1 features** by leverage: `IAsyncEnumerable` pagination iterator
   (F1) and `Location`/`ETag` on `ClientResult` (F5) are the most-requested client
   ergonomics; the RFC 8288 `Link` header (F7) is cheap and standards-aligned; the
   OpenAPI `operation.Deprecated`/ETag metadata (F8) improves contract fidelity.
5. **Plan the v2 breaking window as one major bump.** V1–V4 (`ILinkAuthorizer` +
   resource, async `ILinkUrlResolver`, formatter reshaping, default `UrlStyle` →
   `PathRelative`) are all breaking and interlock with features F2/F4 — do them
   together so there's a single migration.
6. **Ecosystem is adoption-driven, not correctness-driven.** If growth is the goal,
   HAL Explorer middleware (E1) + a `dotnet new cairn-api` template (E2) are the
   highest-impact; Siren (E3) and `Cairn.Mcp` (E8) are differentiators but larger bets.
