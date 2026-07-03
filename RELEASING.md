# Releasing

Versioning and publishing are fully tag-driven. There is no version number in the repository —
[MinVer](https://github.com/adamralph/minver) computes it from git tags at build time, and the
[Release workflow](.github/workflows/release.yml) publishes whenever a version tag is pushed.

## How a release works

1. Merge PRs into `main` as usual. Every CI build produces preview packages versioned
   `X.Y.(Z+1)-preview.0.<height>` (where `vX.Y.Z` is the latest tag and `<height>` is the number of
   commits since it). They are attached to the CI run as an artifact if you want to try one locally.
2. **Promote the public API surface.** New public members accumulate in each project's
   `PublicAPI.Unshipped.txt` as they are added. Before tagging, move every entry from each
   `src/*/PublicAPI.Unshipped.txt` into that project's `PublicAPI.Shipped.txt`, leaving the
   `#nullable enable` header line at the top of both files (the "unshipped" file ends up with only that
   line). This records the surface you are about to ship as shipped, and is what
   [package validation](#binary-compatibility) checks the next release against. Commit it — it is the
   one repo edit a release needs.

   > The `dotnet` "Add all items in PublicAPI.Unshipped.txt to the shipped API" code fix does this per
   > project; moving the lines by hand is equivalent, since the analyzer compares sets, not order.

3. When you want to ship, tag the commit and push the tag (release notes are auto-generated from PR titles):

   ```sh
   git tag v0.6.0
   git push origin v0.6.0
   ```

4. The Release workflow then, automatically:
   - builds and runs the full test suite as a gate;
   - packs every shippable project at exactly `0.6.0` (MinVer reads the tag), validating each against the
     previous release's package (see below);
   - pushes the `.nupkg` + `.snupkg` packages to NuGet.org;
   - creates the GitHub release with auto-generated notes and the packages attached.
5. **Move the compatibility baseline forward.** After the packages are live, set
   `PackageValidationBaselineVersion` in `src/Directory.Build.props` to the version you just shipped, so
   the next cycle's builds validate against it. Commit it as the first change of the new cycle.

Aside from those two tracked edits (the API promotion and the baseline bump), a release is just a tag —
no version numbers in csproj, no manual GitHub release, no separate "bump version" commit.

### Binary compatibility

`EnablePackageValidation` (in `src/Directory.Build.props`) makes every `dotnet pack` compare the packed
assemblies against `PackageValidationBaselineVersion` (the last released version) downloaded from NuGet.org.
A removed or re-signatured public member fails the pack instead of shipping a `MissingMethodException` to
consumers. When you intend a breaking change, that is a major-version decision — bump the version deliberately
rather than working around the check.

### Pre-releases

Tag with a SemVer pre-release suffix and everything else is identical:

```sh
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

NuGet lists it as a pre-release and the GitHub release is marked as a pre-release automatically.

### Fixing a botched release

- **Workflow failed after the tag was pushed** (flaky test, expired key): fix the cause and re-run
  the workflow from the Actions tab. `--skip-duplicate` makes the NuGet push idempotent.
- **Bad package already on NuGet.org**: packages cannot be deleted, only unlisted. Unlist it on
  nuget.org and ship a fixed `vX.Y.(Z+1)`. Never move or reuse a tag that has been published.

## One-time setup

Publishing uses NuGet.org
[Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the
workflow exchanges its GitHub OIDC token for a short-lived (1-hour) API key at publish time, so
there is no long-lived key to store or rotate.

1. On nuget.org, click your username → **Trusted Publishing** → add a policy:
   - **Repository Owner**: `JonahLargen`
   - **Repository**: `Cairn`
   - **Workflow File**: `release.yml` (file name only, no `.github/workflows/` path)
   - **Environment**: leave empty
2. In the GitHub repo, add a secret under **Settings → Secrets and variables → Actions** named
   `NUGET_USER` containing your nuget.org **profile name** (not your email). The `NuGet/login`
   step reads it.

A freshly created policy can show as *temporarily active* for 7 days until its first successful
publish locks it to the repository; if the window lapses before the first release, just restart it
from the same UI. If a push ever fails with 403, check that the policy is still active and that
`NUGET_USER` matches the nuget.org profile name exactly.
