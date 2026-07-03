# Cairn v1 — Enterprise Review & Roadmap Recommendations

A full-surface review of the library as shipped (v1 on NuGet), conducted July 2026.
Scope: all eight packages, docs, CI/release pipeline, and the competitive landscape.
Baseline health: **build clean (0 warnings), all 348 tests pass on net10.0** — the findings
below are on top of an unusually disciplined codebase for a young library. Nothing found
is critical data corruption; the majors are things enterprise adopters will hit early.

Legend: 🔴 major · 🟡 minor · 🔵 suggestion. File references are to the current `main`.

---

## 1. Bug fixes (verified, ship as a patch release)

### Client (`Cairn.Client`)

- 🔴 **HAL-FORMS templates without `target` are silently dropped.**
  `src/Cairn.Client/HypermediaParser.cs:84` requires `target`; the spec makes it optional
  (absent ⇒ submit to the resource's `self` href). `Cairn.Testing` already implements the
  correct fallback (`src/Cairn.Testing/HypermediaResponse.cs:191-195`) — mirror it.
- 🔴 **Boolean submissions fail validation against options the server itself emitted.**
  Server emits bool options as `"true"`/`"false"` (`HalFormsSchema.cs:97`); client
  stringifies a JSON bool via `JsonElement.ToString()` → `"True"`/`"False"`, failing the
  ordinal membership check (`src/Cairn.Client/CairnClient.cs:252-253`). Use `GetRawText()`
  for non-string scalars or compare case-insensitively.
- 🔴 **Rel comparison is case-sensitive, violating RFC 8288 §2.1.** The server compares
  rels case-insensitively throughout; the client's link/affordance dictionaries use
  `StringComparer.Ordinal` (`LinkMap.cs:9`, `HypermediaParser.cs:31-33`), `curies`
  matching is ordinal (`HypermediaParser.cs:53`, `Resource.cs:70`), and `_embedded`
  lookup is case-sensitive. `HasLink("Self")` fails against a server emitting `self`.
- 🔴 **`ConfigurePrimaryHttpMessageHandler` silently disables the SSRF/link policy.**
  The redirect policy depends on the specific primary handler registered in
  `CairnClientServiceCollectionExtensions.cs:47-52`; any consumer replacing the primary
  handler (proxies, mTLS, HTTP/2 pooling — all common) re-enables in-handler
  auto-redirects and `LinkPolicyRedirectHandler` never sees another hop. Mutate the
  existing handler via `ConfigureAdditionalHttpMessageHandlers`-style hooks instead, and
  fail loudly if the handler observes an already-followed redirect.
- 🔴 **Every response is fully double-buffered with no size cap** —
  `CairnClient.cs:60,92,435,470` (buffered read → `ReadAsByteArrayAsync` copy →
  `JsonDocument.Parse`). For a client that threat-models hostile servers, a
  2 GB body is accepted. Use `ResponseHeadersRead` + `JsonDocument.ParseAsync(stream)`
  and/or document `MaxResponseContentBufferSize`.
- 🟡 **Redirect handler forwards `Cookie`/`Proxy-Authorization` cross-origin**
  (`LinkPolicyRedirectHandler.cs:38-47`) — it strips `Authorization` only; match
  `HttpClientHandler` redirect behavior.
- 🟡 **Bind failures masquerade as "not valid JSON"** (`CairnClient.cs:442-451`), and
  `NotSupportedException` escapes the documented no-throw contract.
- 🟡 **`UriTemplate.Stringify`** emits `True`/`False` for bools and locale-formatted
  `DateTime` (`UriTemplate.cs:366-372`) — lowercase bools, use round-trip `"O"` format.
- 🟡 **HAL-FORMS parse round-trip loses data**: field `value` never parsed and absent from
  `AffordanceField`; option `prompt`s discarded; `options.link`, `selectedValues`,
  `minLength`, `step`, `cols`/`rows`, property-level `templated` unparsed
  (`HypermediaParser.cs:148-187`).

### Server (`Cairn.AspNetCore` / `Cairn.Core`)

- 🔴 **Content negotiation is order-dependent and ignores media-range specificity**
  (`Internal/CairnLinkRecorder.cs:335-355`). `Accept: */*, application/hal+json` (both
  q=1) serves the default format because the first tie wins; RFC 9110 §12.5.1 requires
  exact type > `application/*+json` > `application/*` > `*/*`. Also: `q=0` does not
  exclude a format (`:338`), and `application/*+json` can select plain
  `application/json`, which is outside the requested range (`:413-417`).
- 🔴 **HAL-FORMS field names ignore the app's JSON contract** (`Internal/HalFormsSchema.cs:44`):
  camelCase is hardcoded; `[JsonPropertyName]` and `PropertyNamingPolicy` (snake_case
  shops) are ignored, so generic clients build payloads whose fields the endpoint
  silently drops. Resolve names via the serializer's `JsonTypeInfo`.
- 🔴 **HAL-FORMS schema cache freezes the first request's culture**
  (`HalFormsSchema.cs:14,18,46`) — `DisplayAttribute.ResourceType` prompts are resolved
  once per process; a French caller can see German prompts forever. Key the cache by
  culture or resolve prompts per request.
- 🔴 **DTOs with custom `JsonConverter`s lose all hypermedia silently, and the diagnostic
  blames the wrong thing** (`Internal/CairnLinkInjectionModifier.cs:44-47` +
  `CairnLinkRecorder.cs:583-585` — the emit-miss message points at deferred LINQ).
  Detect the contract-kind mismatch and say so.
- 🔴 **OpenAPI schema pollution**: `_links`/`_embedded`/`_actions`/`_templates` are
  injected into *every* object contract, so `AddOpenApi` documents four phantom
  properties on every schema — including request bodies and unconfigured DTOs
  (`CairnLinkInjectionModifier.cs:49-52`; acknowledged in
  `src/Cairn.OpenApi/HypermediaSchemaTransformer.cs:20-23`). Strip placeholders
  (identifiable via `AttributeProvider is null`) from non-linked types and all request
  schemas.
- 🔴 **`UseCairnOptionsHandler` can hijack CORS preflights and answers before authz**
  (`Internal/CairnOptionsMiddleware.cs:25-41`) — skip requests carrying
  `Access-Control-Request-Method`; document ordering relative to `UseCors`.
- 🟡 **`Deprecation: true` is invalid under final RFC 9745** (published March 2025 —
  the boolean form died with the draft). `CairnDeprecationExtensions.cs:42-44` emits it
  when no date is given; default to a date or obsolete the date-less overload.
- 🟡 **`If-None-Match` on unsafe methods unhandled** — `PUT … If-None-Match: *`
  (create-only idiom) has no support; RFC 9110 §13.1.2 requires 412 on match
  (`CairnPreconditions.cs`, `CairnETagExtensions.cs:45-49`).
- 🟡 **`WithETag` can 500 at request time** on selector values containing `"` or
  non-ASCII (`CairnETagExtensions.cs:71-72` `FormatException`).
- 🟡 **Policy-name typos surface as request-time 500s** — all policies are known at
  registration; pre-validate at startup (`Internal/AuthorizationPolicyLinkAuthorizer.cs:57`).
- 🟡 **`AddCurie` doesn't require `{rel}`** yet emits `templated: true` unconditionally
  (`CairnOptions.cs:159-165` + `CairnLinkRecorder.cs:841`); HAL responses also advertise
  curies for affordance-name prefixes that never appear in a HAL document
  (`CairnLinkRecorder.cs:816`).
- 🟡 **`default(LinkRelation)` is a nullability hole** → `ArgumentNullException`
  mid-serialization (`src/Cairn.Core/LinkRelation.cs:18-35`).
- 🟡 **`HypermediaProblem.WithExtension("status", …)` clobbers the numeric `status`**
  (`HypermediaProblem.cs:84-87`) — reject reserved member names.
- 🟡 **Envelope materialization mutates user objects via reflection** — including
  `init`-only properties on records (`CairnLinkRecorder.cs:660-698`); a cached shared
  envelope gets rewritten from a request thread. Deferred sequences in
  `TypedResults.Ok(...)` are enumerated **twice** (double DB query) with only a
  post-response warning (`CairnLinkRecorder.cs:25-30`).
- 🟡 **`AddPaging`/`AddCursorPaging` lookups use exact runtime type** — subclasses of a
  registered envelope get no links, inconsistent with `LinkConfigRegistry` inheritance
  (`CairnOptions.cs:199-221`).
- 🟡 **Options-level `JsonStringEnumConverter` not detected** — enum form options emit
  numeric values that string-enum endpoints won't bind (`HalFormsSchema.cs:116-119`).
- 🟡 **`minLength` never emitted** despite `[StringLength(MinimumLength=…)]`/`[MinLength]`
  being available (`HalFormsSchema.cs:52`); client validation is asymmetric too.
- 🟡 **URI template edge cases**: explode-map with `;` emits `;key=` instead of `;key`
  for empty values (`UriTemplate.cs:198-201`); prefix modifier on composites and
  reserved operators (`=`, `!`, `@`, `|`) are silently accepted instead of erroring
  (RFC 6570 §2.4.1 processing error).

### Tooling (analyzers / generator / Swashbuckle / Testing)

- 🟡 **Generator: regex route constraints with `{}` corrupt parameter parsing**
  (`RoutesGenerator.cs:296-341` — first-`}` scan; `{slug:regex(^\d{{4}}$)}` drops the
  parameter). Use brace-depth scanning.
- 🟡 **Generator: `app.Map(...)` endpoints are invisible** to the `Routes.*` catalog
  (`RoutesGenerator.cs:210`) though the analyzer handles them; inherited controller
  `[Route]` prefixes also missed (`:106-117`).
- 🟡 **CAIRN002 matches any `.WithLinks()` syntactically** and `LinkConfig<T>` detection
  ignores namespace → false positives with third-party code
  (`MissingLinkConfigAnalyzer.cs:66-86`). CAIRN001 false-positives on MVC
  conventional-route names (`MapControllerRoute(name: …)` never collected).
- 🟡 **Testing package disagrees with the spec and the client**: absent `method` defaults
  to `""` instead of `GET` (`HypermediaResponse.cs:103,197`), and bare-string inline
  options aren't parsed (`:239-257`) though the client accepts them.

### Docs / packaging drift (all confirmed)

- 🔴 **`README.md:17` links `CHANGELOG.md`, which was deleted in #62** — a dead link on
  GitHub *and inside every published package* (README is packed). Point at GitHub
  Releases (release notes are already auto-generated).
- 🔴 **Docs builds are a latent build-breaker**: `docs.yml:26` / `ci.yml:57-58` use
  shallow checkouts — MinVer warns on shallow clones and warnings are errors repo-wide.
  `docfx.json` also doesn't pin `TargetFramework`, so which TFM the API docs describe is
  arbitrary.
- 🟡 `docs/articles/client.md:209` claims collection requests support 304, but
  `GetCollectionAsync` has no `ifNoneMatch` parameter and `CollectionResource` has no
  `ETag` — the 304 branch is unreachable through the public API. Implement it (better)
  or fix the doc.
- 🟡 `docs/articles/packages.md:20` claims Cairn.Testing references Cairn.Core; it is
  deliberately dependency-free.

---

## 2. Spec misapplications — summary of what's wrong vs. what's right

Beyond the individual items above, the shape of the story:

**Wrong or non-conforming**: negotiation tie-breaking/q=0/`*+json` handling (RFC 9110);
client rel case-sensitivity (RFC 8288); `Deprecation: true` (RFC 9745 final); missing
`If-None-Match` on unsafe methods (RFC 9110 §13.1.2); HAL-FORMS optional-`target`
handling in the client; curies without `{rel}`; two URI-template edge cases (RFC 6570);
HAL-FORMS `regex` carries .NET-dialect patterns where the spec implies HTML5/ECMAScript
semantics (works Cairn↔Cairn, breaks cross-implementation — at minimum document it).

**Verified correct (worth advertising)**: If-Match strong / If-None-Match weak comparison
semantics, `*` handling, ETag-on-304, 412/428 bodies; `Vary: Accept` emission and dedup;
HAL curies always-array with `name`+`templated`; single-link vs link-array emission; the
full RFC-relevant link-object property set; `LinkRelation` case-insensitive equality;
HAL-FORMS `contentType` defaulting; RFC 6570 pct-triplet passthrough/astral handling;
RFC 9457 member names and extension handling. The URI template implementation is
notably conformant overall.

**Consider one default change**: HAL-FORMS recommends the sole/primary template use the
reserved `default` key; today a typical single-template document keys by action name
unless `AsDefault()` is called, which generic HAL-FORMS clients won't look up first.

---

## 3. New features the library should have

Ordered by leverage:

1. **Client pagination iterator** — `IAsyncEnumerable<Resource<TItem>>` /
   `IAsyncEnumerable<TItem>` that walks `next` to exhaustion (with page/item caps).
   The single highest-value client DX addition; trivially demoable.
2. **Resource-based authorization** — `RequireAuthorization` is caller-only by design
   (documented honestly at `LinkConfig.cs:97-103`), but
   `AuthorizeAsync(user, resource, policy)` is *the* enterprise authorization idiom.
   `ILinkAuthorizer.AuthorizeAsync(string policy)` cannot grow this non-breakingly —
   design the v2 seam now (see §6).
3. **Gated embeds** — `Embed`/`EmbedMany` accept no `When()`/`RequireAuthorization()`
   (`LinkConfig.cs:55-63`), yet embedding is exactly where data leaks.
4. **Startup options validation** — `ValidateOnStart`-style checks: unknown policy names,
   duplicate formatter media types, forced-but-unregistered formats (all currently
   request-time 500s), `AddCurie` template validation. Also `IOptionsMonitor` or at
   least fail-loudly-on-late-`Configure` (options freeze silently today,
   `CairnServiceCollectionExtensions.cs:37`).
5. **Per-request base-URI resolver** — one global `PublicBaseUri` can't do multi-tenant
   hosts; `TransformUrl` skips pagination links and explicit hrefs, so a tenant rewrite
   covers only part of the document. Add `Func<HttpContext, Uri>`.
6. **`Location`/`ETag` on `ClientResult`** — a 201-with-Location create affordance
   currently gives the caller only a status code (`CairnClient.cs:119-128`).
7. **HAL-FORMS `options.link` (options by reference)** — remote value lists are what real
   forms need (country lists, lookups); Spring supports it; the inline-only model caps out fast.
8. **Localization hook** for prompts/titles (ties into the culture-cache bug above).
9. **HTTP `Link` header emission (RFC 8288)** — cheap, spec-pure, gives HEAD requests and
   generic clients (Ketting) something to chew on.
10. **OpenAPI completeness** — document `ETag`/304/412 on `WithETag` endpoints,
    `Deprecation`/`Sunset` headers, and typed `_embedded` schemas; per-format schema
    variants so `application/hal+json` responses don't advertise `_actions` (see
    `Shared/HypermediaJsonSchemas.cs:36-43` — one schema serves three shapes).
11. **Client observability** — an `ActivitySource` with `link.relation`/`affordance.name`
    tags; the server side is genuinely well instrumented, the client has nothing.
12. **Testing package gaps** — `NotHaveTemplate`, embedded-count assertions, `And`-chain
    continuity from embedded assertions, status/content-type/ETag helpers.

---

## 4. What a Sr dev will complain about after installing

1. **Absolute links trust the `Host` header by default.** Behind a proxy without
   `UseForwardedHeaders`, every link in every body reflects an attacker-controlled Host
   (poisoning + internal hostname leakage). The safe modes exist (`PublicBaseUri`,
   `PathRelative`) but the *default* is the dangerous one and nothing warns. Emit a
   one-time startup warning when neither forwarded-headers nor `PublicBaseUri` is
   configured — or flip the default to `PathRelative` in v2.
2. **Output caching is a footgun.** Code comments and docs claim `Vary: Accept` protects
   OutputCache — ASP.NET Core's OutputCache middleware ignores response `Vary` (needs
   `VaryByHeader("Accept")` policy). Worse: `RequireAuthorization`-gated links
   personalize bodies, so a shared cache can replay admin affordances to anonymous
   users. Needs a dedicated caching docs page + a runtime warn-once when policy-gated
   links meet `IOutputCacheFeature`.
3. **App-wide costs once `AddCairn` is called**: every serialized object in the app pays
   the injected-property getters (AsyncLocal read + `Items` lookups × 4), and the
   startup filter adds an `OnStarting` callback to literally every request. Injected
   contract properties also change strict-binding semantics
   (`JsonUnmappedMemberHandling.Disallow` no longer rejects inbound `_links`).
4. **The AOT/trimming story is implied but not delivered.** `CairnJsonContext` advertises
   "standard Native AOT setup", but no project sets `IsAotCompatible`/`IsTrimmable` and
   none of the reflection (`Activator.CreateInstance`, `MakeGenericType`,
   `assembly.GetTypes()`, `GetProperties`) is annotated — trimming strips envelope
   properties with zero warnings. Either annotate and declare it, or drop the claim.
5. **`HypermediaProblem` bypasses `CustomizeProblemDetails`** — error bodies skip the
   pipeline where teams add `traceId`/correlation IDs, so Cairn's errors are shaped
   differently from the rest of the API. It also takes raw href strings, forfeiting the
   library's own `LinkTarget.Route` machinery on the error path.
6. **`IAsyncEnumerable<T>` responses silently lose links** (by design; warned once in
   logs). Fine as a constraint — but it belongs in the README's "when not to use"
   section, not just a log line.
7. **Per-templated-link linear endpoint scan** — `FindPattern` walks all endpoints per
   `RouteTemplate` link per item per request (`LinkGeneratorUrlResolver.cs:106-119`);
   500 endpoints × 100 items = 50k scans/request. Cache by name with
   `EndpointDataSource.GetChangeToken()` invalidation.
8. **Packaging/repo gaps** (all absent): `EnablePackageValidation` +
   `PackageValidationBaselineVersion` (nothing catches binary breaks vs last shipped
   version), package icon, `SECURITY.md`, `CONTRIBUTING.md`, dependabot (NuGet + action
   pins), issue templates, SHA-pinned actions (third-party `NuGet/login@v1` sits on the
   OIDC publishing path with `id-token: write`), coverage collection (coverlet is
   referenced but never invoked), CI `concurrency` group, RELEASING.md missing the
   PublicAPI Unshipped→Shipped promotion step.
9. **Strong naming: decide now.** Defensible to skip on net8+, but impossible to add
   post-1.0 without an identity break; some enterprise NuGet gatekeeping still requires
   it. Document the decision either way.

---

## 5. Features competitors have that Cairn doesn't

Context: the .NET hypermedia space is a graveyard (Halcyon, WebApi.Hal, SirenDotNet,
RiskFirst.Hateoas — all dead/dormant), and they died of the diseases Cairn already
cured: model wrapping, pipeline takeover, single-maintainer opacity. The live
competition is Spring HATEOAS (JVM), JsonApiDotNetCore, and "just use GraphQL/OData"
pressure.

| Gap | Who has it | Verdict |
| --- | --- | --- |
| **Browsable explorer UI** | Spring (HAL Explorer auto-configured at API root) | **Do it.** Cairn already emits compatible HAL/HAL-FORMS; a small middleware serving HAL Explorer + a hosted live sample is the highest-conversion demo in this space. |
| **Siren format** | D2L, dead .NET libs | **Do it.** Siren's typed-field `actions` map 1:1 onto Cairn affordances; every .NET Siren lib is dead — free vacancy, and the `IHypermediaFormatter` SPI mostly exists (but see §6 on its limits). |
| **ALPS profile generation** | Spring auto-publishes ALPS | **Do it (cheap).** Cairn already knows every rel, method, and field; generating the profile doc is nearly free and doubles as agent-readable semantics. |
| **Multi-hop client traversal** | Spring Traverson, JS traverson/Ketting | Sugar over `FollowAsync` — `Follow("orders", "next", "item")`. Low cost. |
| **TypeScript/browser client** | wertzui/HAL (Angular pkg), Ketting (React) | Hypermedia's payoff is rendered UI; at minimum publish a docs page proving Ketting interop against a Cairn API. |
| **Query-side story** (filter/sort/sparse fieldsets) | JsonApiDotNetCore, OData | Blunt it, don't build it: a small opt-in query-param → pagination-envelope binding, or an OData-paging integration sample. Evaluations are lost on `?filter=&sort=` alone. |
| **CRUD scaffolding / template** | JsonApiDotNetCore (models→API), Spring Data REST | A `dotnet new cairn-api` template is the immediate-productivity hook; ideology doesn't drive adoption, deleted boilerplate does. |
| **JSON:API format** | JsonApiDotNetCore | **Skip for now** — they own it in .NET and the full spec is heavy. Note: the current formatter SPI *cannot* express JSON:API's document reshaping anyway (§6). |
| Collection+JSON / UBER / Mason | Spring (barely used) | **Skip.** Community fatigue with generic hypermedia types is real. |

Standards watch: HAL remains an expired draft used as de-facto standard; HAL-FORMS is
still a working draft under `application/prs.hal-forms+json`; **RFC 9745 (Deprecation)
went final in March 2025** — Cairn's boolean emission is now formally invalid (§1).

---

## 6. Wildcard — the strategic bets

### a) `Cairn.Mcp`: affordances as agent tools (first-mover, any ecosystem)

The 2025-26 hypermedia thesis is that HATEOAS was waiting for AI agents: runtime tool
discovery, contextual capability exposure, provider-controlled exploration. A Cairn
response *is already* a dynamically-scoped tool list — state-gated, authorization-gated,
with human-readable titles and typed HAL-FORMS fields. A package that exposes an MCP
server whose per-session tools are the *currently valid* affordances of the resources an
agent has traversed would be a first-mover feature in any ecosystem, not just .NET.
Nobody in .NET has this. HAL-FORMS field schemas map directly onto MCP tool input
schemas; `When()`/`RequireAuthorization()` become the tool-visibility filter.

### b) Positioning: "the missing piece of Microsoft's own API guidance"

Microsoft's API design guidance recommends HATEOAS; ASP.NET Core ships nothing for it.
That one sentence is the wedge. Pair it with: a hosted sample browsable in HAL Explorer,
an honest "Cairn vs GraphQL vs OData" comparison page (the objection every evaluation
raises), a FastEndpoints integration sample (their community has no hypermedia story),
and the htmx-renaissance framing — "hypermedia for your JSON APIs, the way htmx is
hypermedia for your HTML; AI agents are the new machine client."

### c) Fix the v2 extensibility seams while the user base is small

Three interfaces will block the roadmap above and can only change breakingly:

1. `ILinkAuthorizer.AuthorizeAsync(string policy)` — no `ClaimsPrincipal`, no resource
   parameter ⇒ resource-based auth (§3.2) can never be added compatibly.
2. `ILinkUrlResolver.Resolve(LinkTarget)` — synchronous, no request context ⇒ signed
   URLs, tenant lookups, async resolution all impossible.
3. `IHypermediaFormatter` — can inject properties but cannot **reshape the document**
   (`Properties` read once at startup) ⇒ JSON:API and full Siren are inexpressible;
   "custom formats" currently oversells. `HypermediaFormat` being a public enum on
   `CairnOptions.DefaultFormat` also means built-ins can't become formatter-based
   without churn.

Redesigning these seams (plus the default-URL-style decision in §4.1 and gated embeds)
is the core of a v2 breaking window. Everything else in this review is non-breaking.

---

## Suggested sequencing

1. **v1.0.x patches (now)** — §1 bugs (client spec fixes, negotiation, CORS-preflight
   skip, RFC 9745 emission, converter diagnostic), dead CHANGELOG link, docs-build
   shallow-clone fix, packaging hygiene (icon, validation, dependabot, SECURITY.md,
   SHA-pinned actions).
2. **v1.1 (weeks)** — client pagination iterator, `Location`/`ETag` on results,
   collection conditional requests (docs already promise them), startup options
   validation, HAL-FORMS naming-policy + culture fixes, `minLength`, Host-header
   startup warning, OutputCache docs page + guardrail, endpoint-pattern cache,
   HAL Explorer middleware + hosted demo, `dotnet new` template.
3. **v1.2 (quarter)** — Siren formatter (validates the reworked SPI in preview), ALPS
   generation, `Link` header emission, `options.link`, client OTel, Traverson-style
   sugar, Ketting interop docs, GraphQL/OData comparison page.
4. **v2 (design now, ship when ready)** — authorizer/resolver/formatter seam redesign,
   resource-based authorization, gated embeds, per-request base URI, PathRelative (or
   warned-Absolute) default, AOT/trimming posture for Core+Client, `Cairn.Mcp` flagship.
