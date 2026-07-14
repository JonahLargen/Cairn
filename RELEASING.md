# Releasing

Versioning and publishing are fully tag-driven. There is no version number in the repository —
[MinVer](https://github.com/adamralph/minver) computes it from git tags at build time, and the
[Release workflow](.github/workflows/release.yml) publishes whenever a version tag is pushed.

## How a release works

1. Merge PRs into `main` as usual. Every CI build produces preview packages versioned
   `X.Y.(Z+1)-preview.0.<height>` (where `vX.Y.Z` is the latest tag and `<height>` is the number of
   commits since it). They are attached to the CI run as an artifact if you want to try one locally.
2. When you want to ship, tag the commit and push the tag (release notes are auto-generated from PR titles):

   ```sh
   git tag v0.6.0
   git push origin v0.6.0
   ```

3. The Release workflow then, automatically:
   - builds and runs the full test suite as a gate;
   - packs every shippable project at exactly `0.6.0` (MinVer reads the tag), validating each against the
     previous release's package (see below);
   - pushes the `.nupkg` + `.snupkg` packages to NuGet.org;
   - creates the GitHub release with auto-generated notes and the packages attached;
   - generates signed [SLSA build provenance](https://slsa.dev/spec/v1.0/provenance) for the
     packages, records it in GitHub's attestation store, and attaches it to the release as
     `multiple.intoto.jsonl` / `multiple.sigstore.json` (see below).
4. **Move the compatibility baseline forward — after, and only after, the packages are live.** Once the
   Release workflow is green and `0.6.0` is on NuGet.org, open a follow-up PR that sets
   `PackageValidationBaselineVersion` in `src/Directory.Build.props` to the version you just shipped and
   merge it to `main`. This PR ships nothing — it is bookkeeping that points the *next* cycle's builds at the
   release you just made, so an accidental binary break is caught against it.

   **Order matters.** The baseline must name an already-published package, so the sequence is always: push
   the tag → wait for the release to finish publishing → *then* open the baseline PR. Opening it earlier
   makes its own CI pack against a version NuGet cannot download yet, which fails with `NU1011`.

   If last cycle carried a `CompatibilitySuppressions.xml` for an intentional break, delete it in this same
   PR — the new baseline already contains that API, so the entries are stale.

Aside from that one tracked edit (the baseline bump), a release is just a tag — no version numbers in
csproj, no manual GitHub release, no separate "bump version" commit.

### Binary compatibility

`EnablePackageValidation` (in `src/Directory.Build.props`) makes every `dotnet pack` compare the packed
assemblies against `PackageValidationBaselineVersion` (the last released version) downloaded from NuGet.org.
A removed or re-signatured public member fails the pack instead of shipping a `MissingMethodException` to
consumers. Additive changes — new members, types, or overloads (including default interface methods) — pass;
only breaks are flagged.

**Shipping an intentional breaking change.** The baseline is a published NuGet package, so you cannot bump it
to a version that does not exist yet. A `CompatibilitySuppressions.xml` bridges that gap: it records the
breaks you accept so the pack goes green now, and is removed once the baseline catches up. The full cycle:

1. Make the change, then generate the acknowledgement file and commit it alongside the change:

   ```sh
   dotnet pack Cairn.slnx -c Release /p:ApiCompatGenerateSuppressionFile=true
   ```

   This writes `src/Cairn.<Package>/CompatibilitySuppressions.xml` listing each accepted diff (`CP0002` for a
   removed/changed member, `CP0006` for an added interface member). The suppression is scoped to exactly those
   diffs — any *other* break still fails the pack. With it committed, both PR CI and the release pack are green.
2. Release as usual (below). The release still validates against the old baseline, so the file must be present
   for the packages to publish.
3. In the step-4 baseline bump, the new baseline already contains the changed API, so the suppression entries
   are now stale — delete the file(s) in that same commit.

This is deliberate, not a silent workaround: the check still forces every break to be recorded, and pre-1.0
(`0.x`) breaks are allowed under SemVer as long as they are intentional.

### Build provenance

Each release ships signed [SLSA](https://slsa.dev) provenance for the packages, produced by
[GitHub Artifact Attestations](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations)
(`actions/attest-build-provenance`), stating that this repository's Release workflow built the
attached packages from a specific commit. Signing is keyless (Sigstore, via the workflow's OIDC
identity), so there is no signing key to store or rotate, and nothing extra to do when releasing.
The attestation is recorded in GitHub's attestation store and also attached to the release as
`multiple.intoto.jsonl` (the DSSE-wrapped in-toto statement) and `multiple.sigstore.json` (the full
Sigstore bundle, for offline verification).

Anyone can verify a downloaded package with the [GitHub CLI](https://cli.github.com/):

```sh
gh attestation verify Cairn.X.Y.Z.nupkg --repo JonahLargen/Cairn
```

(offline, against the release asset: add `--bundle multiple.sigstore.json`).

### Pre-releases

Tag with a SemVer pre-release suffix and everything else is identical:

```sh
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

NuGet lists it as a pre-release and the GitHub release is marked as a pre-release automatically.

### Fixing a botched release

- **Workflow failed after the tag was pushed** (flaky test, expired key): fix the cause and re-run
  the workflow from the Actions tab. `--skip-duplicate` makes the NuGet push idempotent, and the
  release-creation step is skipped when the release already exists. A re-run records an additional
  attestation in GitHub's attestation store, which is harmless.
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
