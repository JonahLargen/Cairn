# Cairn — Enterprise Review, Second Edition (post-fix-wave)

Reviewed at `main` = `aba3217` (July 2026), i.e. after PRs **#66** (client fidelity/
hardening), **#67** (server spec-correctness), **#68** (docs/packaging drift), and
**#69** (flaky-test fix). Every claimed fix was re-verified against the current code
and its regression tests; the diffs were also reviewed for newly introduced bugs.

Baseline health: **build clean (0 warnings), 412 tests pass on net10.0** (up from 348 —
the fix wave brought genuine regression coverage, including the OpenAPI-pollution test
the first review asked for).

Release context: tags `v0.6.5`/`v0.6.6` both point at PR #67, so the currently
published packages include the #66/#67 fixes; **#68 and #69 are not yet released.**

Legend: 🔴 major · 🟠 medium · 🟡 minor · 🔵 suggestion.

---

## 1. Status of the first review's findings

**Verified fixed (31 items).** Every bug in the first review's client (9/9), server
(18/18), and docs/packaging-drift (4/4) lists is fixed, correctly, with tests:

- **Client (#66):** target-less HAL-FORMS templates fall back to `self`; boolean
  option validation via `GetRawText()`; RFC 8288 case-insensitive rels everywhere
  (maps, curies, `_embedded`); `UriTemplate` lowercase bools + round-trip date
  formats; full HAL-FORMS field round-trip (`Value`, `MinLength`, `Step`,
  `Cols`/`Rows`, option prompts as `AffordanceFieldOption`, `options.link`,
  `selectedValues`); the redirect/SSRF policy now mutates the final primary handler
  via `PostConfigure` (survives `ConfigurePrimaryHttpMessageHandler`) and fails loudly
  on unseen redirects; `Cookie`/`Proxy-Authorization` stripped cross-origin; responses
  are headers-first and streamed with a real `MaxResponseContentBufferSize`
  enforcement (`CappedBodyStream`) and a timeout budget across the body read; bind
  failures report as bind failures and `NotSupportedException` stays inside the
  result contract.
- **Server (#67):** RFC 9110 negotiation (specificity ranking, q=0 exclusion,
  `application/*+json` no longer selecting plain JSON, DefaultFormat tie-break);
  HAL-FORMS field names from the serializer contract (`[JsonPropertyName]`, naming
  policies); per-culture schema caching for localizable prompts; enum options
  serialized through host options (`JsonStringEnumConverter` honored); `minLength`
  emitted/validated/documented; OpenAPI phantom-property injection scoped to
  types that can carry hypermedia, applied schemas marked `readOnly`, placeholders
  stripped (— but see the regression in §2); accurate converter-DTO diagnostic;
  init-only envelope writes eliminated; CORS preflights pass through the OPTIONS
  handler; RFC 9745-valid `Deprecation` always (`@unix-seconds`; date-less overload
  uses registration time, documented); ETag percent-quoting instead of 500s;
  `If-None-Match` on unsafe methods → 412 including the `*` create-only idiom;
  policy names validated at startup via an `IHostedService`; `AddCurie` requires
  `{rel}` and HAL no longer advertises affordance-only curies;
  `default(LinkRelation)` fails fast; `WithExtension` rejects reserved problem
  members; paging registrations walk base types; the three URI-template edge cases.
- **Docs/packaging (#68):** README dead CHANGELOG link → Releases; docs builds
  fetch full history + pinned docfx TFM; collection conditional requests actually
  implemented (`GetCollectionAsync(ifNoneMatch)`, `CollectionResource.ETag`) so the
  docs claim is now true; packages.md corrected.
- **Flaky test (#69):** a real synchronization fix (TCS-signalled log capture + an
  outermost-middleware completion barrier for negative assertions), not a
  sleep/retry hack; it also closed the inverse false-pass race the flake report
  didn't cover.

**Resolved as-designed (1).** Deferred sequences inside immutable results
(`TypedResults.Ok(query)`) can still enumerate twice, but the diagnostic now fires
eagerly at request time and says the right thing — the review asked for detection,
and detection is what shipped.

**Everything else from the first review remains open** — §3 below is the
re-verified list.

---

## 2. New issues introduced by the fix wave

- 🟠 **Injection scoping regression: derived-only configs behind a base-typed
  declaration lose all links** — `src/Cairn.AspNetCore/Internal/CairnLinkInjectionModifier.cs:63-67`.
  `CanCarryHypermedia` is evaluated against the *declared* type when the serializer
  contract is built, but the compute stage dispatches on the *runtime* type. A
  `List<BaseAnimal>` holding `DerivedDog` items, where only `DerivedDog` has a
  `LinkConfig`, records links that can never be emitted. **Empirically bisected:
  passes at `3b9e051^`, fails at `3b9e051`.** Two aggravations: the post-response
  diagnostic then emits the misleading "deferred sequence" message
  (`CairnLinkRecorder.cs:706-708`), and a `LinkConfig` registered *after* a type's
  contract is first built (the registry explicitly supports late `Add`) permanently
  loses emission for that type. Both worked before the PR because injection was
  unconditional. Fix: also inject when any *derivable* config could apply (e.g.
  non-sealed declared types), or dispatch `CanCarryHypermedia` on runtime types
  observed at compute time and invalidate the contract cache.
- 🟠 **The redirect fail-loudly check false-positives on URI-rewriting inner
  handlers** — `src/Cairn.Client/LinkPolicyRedirectHandler.cs:70-77`.
  `EnsureRedirectsAreVisible` throws whenever `response.RequestMessage.RequestUri`
  differs from the sent URI. A consumer handler that mutates the request URI (e.g.
  appends an API-key query parameter — a common pattern) makes **every request**
  throw, with a message that misdiagnoses the cause. *Empirically confirmed on a
  plain 200 GET.* Also: that throw path doesn't dispose the just-received response
  (the rejected-redirect path at `:26` does). Fix: compare minus the query, or
  carve out same-origin, and dispose before throwing.
- 🔴 **`GetCollectionAsync` change is binary- and source-breaking — not yet
  shipped, decide before the next release** — `src/Cairn.Client/CairnClient.cs`
  (PR #68). Inserting `string? ifNoneMatch = null` before the `CancellationToken`
  changes the signature `(string, string, CancellationToken)` →
  `(string, string, string?, CancellationToken)`: assemblies compiled against
  v0.6.6 throw `MissingMethodException` at runtime, and positional
  `GetCollectionAsync<T>(url, "items", ct)` callers no longer compile. Since
  v0.6.6 = `3b9e051`, this break has **not** shipped yet — release a side-by-side
  overload instead (or accept the break deliberately at 0.x and release-note it).
  The meta-finding: nothing caught this because `PublicAPI.Shipped.txt` was never
  promoted (the whole shipped surface still sits in Unshipped) and
  `EnablePackageValidation` is absent — both still-open items from the first
  review, now with a concrete bite mark.
- 🟡 **Startup policy validator can false-positive on dynamic policy providers**
  — `src/Cairn.AspNetCore/Internal/AuthorizationPolicyStartupValidator.cs:60`.
  The `DefaultAuthorizationService` guard covers replaced services, not replaced
  *providers*: a custom `IAuthorizationPolicyProvider` that materializes policies
  after boot (store-backed) fails `StartAsync` for a policy that would work at
  request time; and `GetPolicyAsync` is awaited unguarded, so a throwing provider
  crashes startup without the curated message. Add an opt-out and a try/catch.
- 🟡 **Unbounded per-culture schema cache growth** —
  `src/Cairn.AspNetCore/Internal/HalFormsSchema.cs:22,38,45`. The cache key uses raw
  `CurrentUICulture.Name`; a host that sets UI culture straight from
  `Accept-Language` (arbitrary culture strings) grows one cached list per distinct
  culture per localizable input type, forever. Clamp to a bounded set or LRU.
- 🟡 **Templated `self` used verbatim as a template-target fallback** —
  `src/Cairn.Client/HypermediaParser.cs:84`. The new fallback takes
  `selfLinks[0].Href` without checking `Templated`; a target-less template plus
  `"self": {"href": "/orders/{id}", "templated": true}` produces an affordance that
  submits literal braces. Expand-with-no-variables or skip.
- 🟡 **Release-note the new fail-fast behaviors.** Correct per spec, but upgrading
  consumers can newly throw where v0.6.x was silently wrong: `UriTemplate` now
  raises `FormatException` for prefix-on-composite and op-reserve operators,
  `AddCurie` without `{rel}` throws at startup, and `default(LinkRelation)` throws
  at construction. All deliberate — say so in the release notes.
- 🔵 Nits, recorded for completeness: client maps now collapse rels differing only
  by case (last-wins replace, not merge — a sloppy server can hide a link);
  timeout-budget cancellation surfaces as a bare `OperationCanceledException`
  rather than the `TaskCanceledException`+`TimeoutException` convention;
  duplicate identical Accept ranges take the first q rather than the highest;
  Accept parameters beyond `q` are ignored when ranking (over-selects only);
  a curied affordance renamed by `AsDefault()` still advertises its curie;
  the negative-assertion test barrier (`DiagnosticTiming.ResponseCompletion`)
  arms one waiter at a time and relies on `OnCompleted` LIFO ordering —
  acknowledged in its comments, fine for current use.

---

## 3. Still open from the first review (re-verified against current `main`)

### Tooling — untouched by the fix wave; all fourteen findings stand

The four PRs changed nothing under `src/Cairn.Analyzers`, `src/Cairn.SourceGenerators`,
`src/Cairn.CodeFixes`, `src/Cairn.Swashbuckle`, or `src/Cairn.Testing`.

- 🔴 **CAIRN001 never validates `LinkTarget.RouteTemplate(...)`**
  (`RouteNameAnalyzer.cs:82` — `method == "Route"` only). Identical runtime failure
  mode on typos; no test covers it.
- 🔴 **The CAIRN001 code fix is invisible in IDEs** — compilation-end diagnostics
  get no lightbulb (dotnet/roslyn#24827); the fix only fires in tests/batch tools.
  Restructure (compilation-start-cached name index consulted from the node action)
  or document the limitation.
- 🔴 **A route named `Routes` generates uncompilable code** (CS0542); routes named
  `Equals`/`GetHashCode`/`ToString` hide `object` members (CS0108 — fatal under
  consumer `TreatWarningsAsErrors`). `RoutesGenerator.cs` `Sanitize` (~`:471`) and
  the emission loop (`:385-404`) guard route-vs-route collisions only.
- 🔴 **Cairn.Swashbuckle requires Swashbuckle.AspNetCore ≥ 10.x** — the
  `IOpenApiSchema` filter signature is incompatible with 6.x–9.x where most of the
  installed base lives; nothing documents the floor.
- 🟡 **CAIRN002 misses the MVC opt-in path** — `[CairnLinks]` actions never
  analyzed; `var ep = app.MapGet(...); ep.WithLinks();` silently skipped; no
  cross-project escape hatch (`MissingLinkConfigAnalyzer.cs:76-95`).
- 🟡 **Named-argument reordering defeats CAIRN001**
  (`LinkTarget.Route(routeValues: v, routeName: "x")` skipped, ~`:226-236`);
  receiver-blind `WithName` collection even counts `LinkTarget.WithName(...)` (a HAL
  link name) as a declared route.
- 🟡 **Analyzer hygiene**: `HashSet<(ITypeSymbol, Location)>` without
  `SymbolEqualityComparer` (`MissingLinkConfigAnalyzer.cs:225`); no `helpLinkUri` on
  any descriptor; `AnalyzerReleases.Shipped.md` still empty though the rules are
  published (fold promotion into RELEASING.md).
- 🟡 **Generator**: regex constraints with `{}` corrupt parameter parsing
  (first-`}` scan, `RoutesGenerator.cs:298`); `app.Map(...)`/`MapFallback` invisible
  to the catalog (`:210`) though the analyzer covers them; inherited controller
  `[Route]` prefixes missed; CAIRN003 reported at `Location.None`.
- 🟡 **CAIRN002 matches any `.WithLinks()` syntactically**, `LinkConfig<T>`
  detection is namespace-blind, and CAIRN001 false-positives on MVC
  conventional-route names (`MapControllerRoute(name: …)` never collected).
- 🟡 **Cairn.Testing**: `Parse` on a bare JSON-array root — Cairn's own collection
  wire shape — silently returns an all-empty response (`HypermediaResponse.cs:59-75`;
  add `ParseAll`, throw on array in `Parse`); absent `method` defaults to `""`
  instead of the spec's `GET` (`:103,:197`); bare-string inline options dropped
  (`:239-257`); link `type`/`deprecation`/`hreflang`/`profile` unparsed and
  therefore unassertable.

### Enterprise/server posture — open except one partial

- 🔴 **Absolute links trust the `Host` header by default.** Behind a proxy without
  `UseForwardedHeaders`, every link reflects an attacker-controlled Host. The safe
  modes exist (`PublicBaseUri`, `PathRelative`) but nothing warns when neither is
  configured. One-time startup warning, or default to `PathRelative` in a breaking
  window.
- 🔴 **OutputCache is a footgun.** `CairnLinkRecorder.cs:255` still claims `Vary:
  Accept` protects OutputCache — that middleware ignores response `Vary` (needs a
  `VaryByHeader("Accept")` policy) — and policy-gated links personalize bodies, so a
  shared cache can replay privileged affordances to anonymous users. Needs a caching
  docs page + a warn-once when policy-gated links meet `IOutputCacheFeature`.
- 🟡 **App-wide costs — partially addressed by #67.** Injection is now scoped to
  types that can carry hypermedia, so unconfigured DTOs no longer pay the
  injected-getter cost and the `JsonUnmappedMemberHandling.Disallow` semantic change
  is confined to configured types. Still open: the startup filter registers an
  `OnStarting` callback on literally every request, deprecation metadata or not
  (`CairnServiceCollectionExtensions.cs:36`, `CairnHeadersMiddleware.cs:18`).
- 🟡 **Per-templated-link linear endpoint scan** —
  `LinkGeneratorUrlResolver.cs:106-119` still walks all endpoints per
  `RouteTemplate` link per item per request. Cache by name, invalidate with
  `EndpointDataSource.GetChangeToken()`.
- 🟡 **AOT/trimming story implied but not delivered** — `CairnJsonContext`
  advertises Native AOT; no `IsAotCompatible`/`IsTrimmable` anywhere, reflection
  unannotated. Annotate or drop the claim.
- 🟡 **`HypermediaProblem` bypasses `CustomizeProblemDetails`** — error bodies skip
  the pipeline where teams add `traceId`; still takes raw href strings rather than
  `LinkTarget.Route`.
- 🟡 **`IAsyncEnumerable<T>` responses silently lose links** — by design, warned in
  logs, but still absent from the README's "when not to use" framing.
- 🟡 **Options freeze silently** — no `IOptionsMonitor`, and `Configure<CairnOptions>`
  after first resolution is ignored (`CairnServiceCollectionExtensions.cs:37`).
  #67's policy validator fixed the worst request-time-500 case; formatter/config
  late-registration validation is still absent.

### Packaging / repo hygiene — all open, individually re-verified

No `PackageIcon`; no `EnablePackageValidation`/`PackageValidationBaselineVersion`
(§2's binary break is the cost of this, made concrete); no `SECURITY.md`,
`CONTRIBUTING.md`, dependabot, or issue templates; workflow actions tag-pinned
rather than SHA-pinned — including third-party `NuGet/login@v1` on the OIDC
publishing path (`release.yml:50`, `id-token: write`); coverlet referenced but
coverage never collected; no `concurrency` group in `ci.yml`; RELEASING.md still
omits the PublicAPI Unshipped→Shipped promotion (also now with a §2 bite mark);
strong-naming decision undocumented; analyzers ship only inside Cairn.AspNetCore
(Core-only contract assemblies get no CAIRN001/002) via hardcoded
`bin\$(Configuration)\netstandard2.0\` pack paths.

---

## 4. Spec-conformance summary (post-#66/#67)

**Now verified conformant** (moved from the first review's "wrong" column):
negotiation specificity/q=0/`*+json` (RFC 9110 §12.5.1); client rel
case-insensitivity (RFC 8288 §2.1); `Deprecation: @unix-seconds` (RFC 9745 final);
`If-None-Match` on unsafe methods → 412 (RFC 9110 §13.1.2); HAL-FORMS optional
`target` in the client; curies with required `{rel}`; the RFC 6570 edge cases
(`;` ifemp in exploded maps, prefix-on-composite and op-reserve now processing
errors). Already-correct strengths stand: If-Match/If-None-Match comparison
semantics, ETag-on-304, 412/428 bodies, `Vary: Accept` emission, link-object
property set, HAL-FORMS `contentType` defaulting, RFC 9457 member handling.

**Remaining spec notes**: HAL-FORMS `regex` still carries .NET-dialect patterns
where the spec implies ECMAScript semantics (fine Cairn↔Cairn; document it);
the sole/primary template still isn't keyed `default` unless `AsDefault()` is
declared (HAL-FORMS clients look `default` up first); no 406 path (legal —
"SHOULD send 406 *or* disregard" — worth a docs sentence); Accept parameters
beyond `q` ignored in ranking (§2 nit).

---

## 5. Feature gaps (unchanged priorities, minus what #68 delivered)

Collection conditional requests shipped in #68 (modulo the §2 signature concern).
The rest of the first review's list stands, ordered by leverage:

1. **Client pagination iterator** — `IAsyncEnumerable<Resource<TItem>>` walking
   `next` to exhaustion. Highest-value client DX addition.
2. **Resource-based authorization** — `AuthorizeAsync(user, resource, policy)`;
   requires the v2 `ILinkAuthorizer` seam (§7).
3. **Gated embeds** — `Embed`/`EmbedMany` still accept no
   `When()`/`RequireAuthorization()`, yet embedding is where data leaks.
4. **Per-request base-URI resolver** — multi-tenant hosts; `TransformUrl` still
   skips pagination links and explicit hrefs.
5. **`Location`/`ETag` on `ClientResult`** — a 201-create affordance still yields
   only a status code.
6. **HAL-FORMS `options.link`** (options by reference) — remote value lists.
7. **HTTP `Link` header emission** (RFC 8288) — cheap, spec-pure, useful for HEAD.
8. **OpenAPI completeness** — `operation.Deprecated` + `Deprecation`/`Sunset`
   header docs (metadata already discoverable); `ETagMetadata` so `WithETag`
   endpoints document `ETag`/304/412; `IEndpointMetadataProvider` on
   `HypermediaProblem`; typed `_embedded` schemas; per-format schema variants
   (one schema still serves three shapes, `Shared/HypermediaJsonSchemas.cs:36-43`).
9. **Client observability** — an `ActivitySource` with `link.relation` /
   `affordance.name` tags; server-side OTel is done, client has none.
10. **Testing surface** — `NotHaveTemplate`, embedded-count assertions, `And`-chain
    continuity, status/content-type/ETag helpers (plus the §3 parser fixes).

---

## 6. Competitive positioning (unchanged from the first review)

The .NET hypermedia space is a graveyard whose corpses died of model-wrapping,
pipeline takeover, and maintenance opacity — all things Cairn avoids. Live pressure
is Spring HATEOAS, JsonApiDotNetCore, and "just use GraphQL/OData."

Do: **HAL Explorer compatibility + hosted browsable demo** (highest-conversion
move in this space); **Siren formatter** (typed-field `actions` map 1:1 onto
affordances; every .NET Siren lib is dead); **ALPS profile generation** (nearly
free — Cairn knows every rel/method/field); `dotnet new cairn-api` template;
Traverson-style multi-hop client sugar; Ketting-interop docs page; an honest
"Cairn vs GraphQL vs OData" comparison. Blunt the query-side objection with a
small opt-in query-param binding or an OData-paging sample.
Skip: JSON:API (owned by JsonApiDotNetCore; inexpressible in the current
formatter SPI anyway), Collection+JSON/UBER/Mason.

Wildcard, unchanged and still first-mover: **`Cairn.Mcp`** — a Cairn response is
already a dynamically-scoped, state- and authorization-gated tool list with
human-readable titles and typed HAL-FORMS field schemas; exposing current
affordances as MCP tools for AI agents has no equivalent in any ecosystem.
Positioning wedge: "the missing piece of Microsoft's own API design guidance,"
riding the htmx-renaissance narrative with AI agents as the new machine client.

---

## 7. v2 seams (unchanged — fix while the user base is small)

1. `ILinkAuthorizer.AuthorizeAsync(string policy)` — no principal, no resource ⇒
   resource-based auth can never be added compatibly.
2. `ILinkUrlResolver.Resolve(LinkTarget)` — sync, no request context ⇒ signed
   URLs, tenant lookups, async resolution impossible.
3. `IHypermediaFormatter` — injects properties but cannot reshape documents
   (`Properties` read once at startup) ⇒ JSON:API and full Siren inexpressible;
   `HypermediaFormat` as a public enum couples built-ins to the options surface.

Plus the two default-posture decisions best made in a breaking window: link URL
style (`PathRelative` vs warned-`Absolute`) and gated embeds.

---

## 8. Suggested sequencing (updated)

1. **Before the next release (v0.6.7 / v0.7.0)** — decide the `GetCollectionAsync`
   break (overload vs release-noted break); fix the 🟠 injection-scoping regression
   and the 🟠 redirect false-positive; promote `PublicAPI.Shipped.txt` and turn on
   `EnablePackageValidation` with a baseline so this class of issue is caught
   mechanically; release-note the new fail-fast behaviors.
2. **Next minor wave** — tooling fixes (RouteTemplate analyzer coverage, `Routes`
   name collision, code-fix visibility decision, Swashbuckle floor documentation,
   Testing parser fixes); Host-header startup warning; OutputCache docs page +
   guardrail; endpoint-pattern cache; packaging hygiene batch (icon, SECURITY.md,
   dependabot, SHA pins, coverage, concurrency).
3. **Feature wave** — client pagination iterator, `Location`/`ETag` on results,
   client OTel, HAL Explorer middleware + hosted demo, `dotnet new` template,
   `options.link`, `Link` header emission, OpenAPI completeness batch.
4. **v2 window** — the three seams, resource-based authorization, gated embeds,
   per-request base URI, URL-style default, AOT/trimming posture, Siren formatter
   (validates the reworked SPI), ALPS generation, `Cairn.Mcp` flagship.
