# Releasing

Versioning and publishing are fully tag-driven. There is no version number in the repository —
[MinVer](https://github.com/adamralph/minver) computes it from git tags at build time, and the
[Release workflow](.github/workflows/release.yml) publishes whenever a version tag is pushed.

## How a release works

1. Merge PRs into `main` as usual. Every CI build produces preview packages versioned
   `X.Y.(Z+1)-preview.0.<height>` (where `vX.Y.Z` is the latest tag and `<height>` is the number of
   commits since it). They are attached to the CI run as an artifact if you want to try one locally.
2. When you want to ship, update the `Unreleased` section of `CHANGELOG.md` into a version heading
   (optional but recommended), then tag the commit and push the tag:

   ```sh
   git tag v0.6.0
   git push origin v0.6.0
   ```

3. The Release workflow then, automatically:
   - builds and runs the full test suite as a gate;
   - packs every shippable project at exactly `0.6.0` (MinVer reads the tag);
   - pushes the `.nupkg` + `.snupkg` packages to NuGet.org;
   - creates the GitHub release with auto-generated notes and the packages attached.

That's it — no csproj edits, no manual GitHub release, no separate "bump version" commit.

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

The workflow needs a `NUGET_API_KEY` repository secret:

1. Create an API key at <https://www.nuget.org/account/apikeys> with the **Push new packages and
   package versions** scope, glob pattern `Cairn.*`.
2. Add it under **Settings → Secrets and variables → Actions** as `NUGET_API_KEY`.

NuGet.org API keys expire after at most a year; when a push fails with 403, regenerate the key and
update the secret. Alternatively, NuGet.org supports
[Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) (OIDC
from GitHub Actions, no long-lived secret) — a worthwhile upgrade once the packages exist.
