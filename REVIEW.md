# Cairn — Action List

Reviewed at `main` = `aba3217`. Released: `v0.6.6` = `3b9e051`.

## Before the next release

- Fix the injection-scoping regression: `CanCarryHypermedia`
  (`src/Cairn.AspNetCore/Internal/CairnLinkInjectionModifier.cs:63-67`) gates on the
  declared type, so a `List<BaseAnimal>` of `DerivedDog` items with only a
  `DerivedDog` config emits no links (worked at `3b9e051^`), and configs registered
  after a contract is first built never emit. Dispatch injection on derivable types
  (inject for non-sealed unconfigured types, or invalidate the contract cache on
  registry change), and make the emit-miss diagnostic name this case instead of
  blaming deferred LINQ (`CairnLinkRecorder.cs:706-708`).
- Fix `EnsureRedirectsAreVisible` (`src/Cairn.Client/LinkPolicyRedirectHandler.cs:70-77`):
  it throws on any inner handler that rewrites the request URI (e.g. appends an
  API-key query parameter), breaking every request. Compare URIs minus the query,
  and dispose the response before throwing.
- Replace the `GetCollectionAsync` signature change (PR #68) with a side-by-side
  overload: inserting `string? ifNoneMatch = null` before the `CancellationToken`
  breaks assemblies compiled against v0.6.6 (`MissingMethodException`) and
  positional-token callers.
- Promote the shipped API surface from `PublicAPI.Unshipped.txt` to
  `PublicAPI.Shipped.txt` in every package, and add the promotion step to
  RELEASING.md.
- Set `EnablePackageValidation=true` in `src/Directory.Build.props` and
  `PackageValidationBaselineVersion=0.6.6`.
- Release-note the new fail-fast behaviors: `UriTemplate` now throws
  `FormatException` on prefix-on-composite and op-reserve operators, `AddCurie`
  requires `{rel}`, `default(LinkRelation)` throws at construction.

## Bugs — server

- Emit a one-time startup warning when `UrlStyle` is `Absolute` and neither
  forwarded headers nor `PublicBaseUri` is configured
  (`Internal/LinkGeneratorUrlResolver.cs:103` trusts the incoming `Host`).
- Add an opt-out and a try/catch around `GetPolicyAsync` in
  `Internal/AuthorizationPolicyStartupValidator.cs:60`; a dynamic
  `IAuthorizationPolicyProvider` that materializes policies after boot currently
  fails startup.
- Bound the per-culture schema cache (`Internal/HalFormsSchema.cs:22,38,45`): the
  key uses raw `CurrentUICulture.Name`, which grows unbounded under
  `Accept-Language`-derived cultures. Cap or LRU it.
- Cache `FindPattern`'s name→pattern lookup
  (`Internal/LinkGeneratorUrlResolver.cs:106-119`), invalidated by
  `EndpointDataSource.GetChangeToken()`; it currently scans all endpoints per
  templated link per item per request.
- Correct the comment at `Internal/CairnLinkRecorder.cs:255`: ASP.NET Core's
  OutputCache ignores response `Vary`. Add a caching docs page (OutputCache needs
  `VaryByHeader("Accept")`; policy-gated links must not be shared-cached) and a
  warn-once when policy-gated links are emitted on a request with
  `IOutputCacheFeature`.
- Skip registering the per-request `OnStarting` callback when no endpoint carries
  deprecation metadata (`Internal/CairnHeadersMiddleware.cs:18`).
- Route `HypermediaProblem` through `IProblemDetailsService` so
  `CustomizeProblemDetails` applies, and accept `LinkTarget` for its links instead
  of raw href strings (`HypermediaProblem.cs`).
- Validate late configuration: fail loudly when `Configure<CairnOptions>` runs
  after first resolution, and validate registered formatter media types at startup
  (`CairnServiceCollectionExtensions.cs:37`).
- Key a sole HAL-FORMS template as `default` even without `AsDefault()`
  (`Internal/CairnLinkInjectionModifier.cs:117`); log on runtime default-key
  collision between `When()`-gated `AsDefault()` affordances.
- Skip emitting a curie for an affordance renamed to `default` by `AsDefault()`
  (`Internal/CairnLinkRecorder.cs:983`).
- Document that HAL-FORMS `regex` carries the .NET pattern verbatim and must be
  ECMAScript-compatible for non-Cairn clients
  (`Internal/HalFormsSchema.cs`, docs/articles/affordances-and-forms.md).
- Add an "`IAsyncEnumerable<T>` responses don't get links" caveat to the README's
  when-to-use section.

## Bugs — client

- Expand a templated `self` link with no variables (or skip the template) in the
  target-less HAL-FORMS fallback (`src/Cairn.Client/HypermediaParser.cs:84`); it
  currently submits literal braces.
- Surface timeout-budget cancellation as `TaskCanceledException` with a
  `TimeoutException` inner, matching `HttpClient` convention
  (`src/Cairn.Client/CairnClient.cs:608-617`).

## Bugs — tooling

- Extend CAIRN001 to `LinkTarget.RouteTemplate(...)`
  (`src/Cairn.Analyzers/RouteNameAnalyzer.cs:82`).
- Restructure CAIRN001 so the code fix works in IDEs: report from the node action
  against a compilation-start-cached route-name index instead of
  `RegisterCompilationEndAction` (compilation-end diagnostics get no lightbulb).
- Guard the generator against a route named `Routes` (CS0542) and against
  `Equals`/`GetHashCode`/`ToString` (CS0108): rename with a prefix and report a
  diagnostic (`src/Cairn.SourceGenerators/RoutesGenerator.cs:385-404,471`).
- Match CAIRN001 arguments by parameter name, not position
  (`RouteNameAnalyzer.cs:226-236`); named-argument reordering currently skips
  validation.
- Symbol-filter `WithName` collection to the ASP.NET builder extension and exclude
  `Cairn.LinkTarget.WithName` (`RouteNameAnalyzer.cs:75-81`).
- Collect `MapControllerRoute`/`MapAreaControllerRoute` route names for CAIRN001.
- Extend CAIRN002 to `[CairnLinks]` controller actions and to `.WithLinks()`
  chains broken through a variable; add a `cairn_additional_configured_types`
  escape hatch (`src/Cairn.Analyzers/MissingLinkConfigAnalyzer.cs:76-95`).
- Bind CAIRN002's `.WithLinks()` match to Cairn's symbol and require
  `LinkConfig<T>`'s namespace to be `Cairn`
  (`MissingLinkConfigAnalyzer.cs:66-86`).
- Use `SymbolEqualityComparer.Default` in the duplicate-report set
  (`MissingLinkConfigAnalyzer.cs:225`).
- Add `helpLinkUri` (a docs anchor per rule) to all three descriptors.
- Move CAIRN001/002/003 to a versioned section of `AnalyzerReleases.Shipped.md`
  at release time; add the step to RELEASING.md.
- Parse route templates with brace-depth scanning so regex constraints containing
  `{}` don't drop parameters (`RoutesGenerator.cs:298`).
- Add `Map` and `MapFallback` to the generator's chain matcher
  (`RoutesGenerator.cs:210`).
- Walk base controller types for inherited `[Route]` prefixes
  (`RoutesGenerator.cs:106-117`).
- Carry the `WithName`/attribute location through `RouteInfo` so CAIRN003 is
  navigable instead of `Location.None` (`RoutesGenerator.cs:398`).
- Document the Swashbuckle.AspNetCore ≥ 10.x floor in the Cairn.Swashbuckle
  package description and docs/articles/packages.md.
- Cairn.Testing (`src/Cairn.Testing/HypermediaResponse.cs`): add
  `ParseAll`/`ReadHypermediaListAsync` for array-root responses and throw on an
  array root in `Parse` (:59-75); default absent `method` to `GET` (:103,:197);
  parse bare-string inline options (:239-257); parse link
  `type`/`deprecation`/`hreflang`/`profile` (:166-178) so they can be asserted.

## Packaging / repo

- Add a package icon (`icon.png` + `<PackageIcon>` in `src/Directory.Build.props`).
- Add `SECURITY.md`, `CONTRIBUTING.md`, `.github/dependabot.yml` (NuGet + actions),
  and `.github/ISSUE_TEMPLATE/`.
- Pin all workflow actions by full SHA, including `NuGet/login` in `release.yml:50`.
- Collect coverage in CI (`--collect:"XPlat Code Coverage"`) or remove the
  coverlet references.
- Add a `concurrency` group to `ci.yml`.
- Skip strong naming; record the decision in CONTRIBUTING.md.
- Guard the analyzer pack items with `Exists()` checks and document that the
  analyzers ship only inside Cairn.AspNetCore
  (`src/Cairn.AspNetCore/Cairn.AspNetCore.csproj:24-26`).
- Enable trim/AOT analyzers (`IsTrimmable`, `EnableTrimAnalyzer`,
  `IsAotCompatible` where achievable), annotate the reflection paths
  (`DynamicallyAccessedMembers`), and align the `CairnJsonContext` AOT claim with
  what is actually supported.

## Features

- Client pagination iterator: `IAsyncEnumerable<Resource<TItem>>` walking `next`
  to exhaustion, with page/item caps.
- Resource-based authorization: `RequireAuthorization` overload that evaluates
  `AuthorizeAsync(user, resource, policy)` (requires the v2 `ILinkAuthorizer`
  seam below).
- `When()`/`RequireAuthorization()` on `Embed`/`EmbedMany`
  (`src/Cairn.Core/LinkConfig.cs:55-63`).
- Per-request base-URI resolver (`Func<HttpContext, Uri>`) for multi-tenant
  hosts; apply `TransformUrl` to pagination links and explicit hrefs.
- Surface `Location` and `ETag` on `ClientResult`
  (`src/Cairn.Client/CairnClient.cs:119-128`).
- HAL-FORMS `options.link` (options by reference).
- Emit the HTTP `Link` header (RFC 8288) alongside body links.
- OpenAPI: set `operation.Deprecated` and document `Deprecation`/`Sunset` headers
  from `DeprecationMetadata`; add `ETagMetadata` to `WithETag` and document
  `ETag`/304/412; implement `IEndpointMetadataProvider` on `HypermediaProblem`;
  typed `_embedded` schemas; per-format schema variants
  (`src/Shared/HypermediaJsonSchemas.cs:36-43`).
- Client `ActivitySource` with `link.relation`/`affordance.name` tags.
- Cairn.Testing assertions: `NotHaveTemplate`, embedded-count, `And`-chain
  continuity from embedded assertions, status/content-type/ETag helpers,
  `WithContentType` on affordance assertions.

## Ecosystem

- Serve HAL Explorer via a small middleware and host a browsable sample API.
- Ship a `dotnet new cairn-api` template.
- Publish a Siren formatter package (`application/vnd.siren+json`).
- Generate ALPS profile documents from registered `LinkConfig<T>`s.
- Add Traverson-style multi-hop client sugar: `Follow("orders", "next", "item")`.
- Publish a Ketting-interop docs page and a "Cairn vs GraphQL vs OData" page.
- Add a minimal opt-in query-parameter → pagination-envelope binding.
- Build `Cairn.Mcp`: expose the current resource's state- and authorization-gated
  affordances as MCP tools (HAL-FORMS field schemas → tool input schemas).

## v2 (breaking window)

- `ILinkAuthorizer`: add `ClaimsPrincipal` and a resource parameter.
- `ILinkUrlResolver`: make async and pass request context.
- `IHypermediaFormatter`: allow document reshaping, not just property injection;
  decouple `CairnOptions.DefaultFormat` from the `HypermediaFormat` enum.
- Default `UrlStyle` to `PathRelative`.
- Declare and annotate the trimming/AOT posture for Cairn.Core and Cairn.Client.
