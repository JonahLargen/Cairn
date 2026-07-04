# Contributing to Cairn

Thanks for your interest in contributing! Issues and pull requests are welcome.

## Getting started

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download) (see
`global.json`), plus the .NET 8 and .NET 9 runtimes to run the multi-targeted
test projects on their lower target frameworks.

```sh
dotnet restore Cairn.slnx
dotnet build Cairn.slnx --configuration Release --no-restore
dotnet test Cairn.slnx --configuration Release --no-build
```

That is exactly what CI runs, so a clean local run means a green build.
Warnings are errors repo-wide (`TreatWarningsAsErrors`), including analyzer,
documentation, and trim-analysis warnings — fix them rather than suppressing
without a justification.

## Making changes

- Open an issue first for anything non-trivial, so the approach can be agreed
  on before you invest time in it.
- Keep pull requests focused: one change per PR.
- Add or update tests for behavior changes; the test suite lives under
  `tests/`. CI enforces a 95% minimum for both line and branch coverage
  (measured with the coverlet collector via `coverage.runsettings`, which
  excludes generated code), and the Codecov project status applies the same
  strictness by counting partially covered lines as uncovered. New code
  needs tests that exercise every branch to keep the build green.
- Update the docs (`docs/articles/`) and XML doc comments when public behavior
  changes.
- Public API changes are validated against the last released packages
  (`EnablePackageValidation`) — an accidental binary-breaking change fails
  `dotnet pack`. Intentional breaks are a major-version decision; raise them in
  the issue first.

## Project layout

| Directory | Contents |
|---|---|
| `src/` | The shipped packages (see `docs/articles/packages.md`). |
| `tests/` | Test projects (xunit). |
| `samples/` | Runnable sample API. |
| `benchmarks/` | BenchmarkDotNet projects. |
| `docs/` | DocFX site published to GitHub Pages. |

The Roslyn components (`Cairn.Analyzers`, `Cairn.CodeFixes`,
`Cairn.SourceGenerators`) have no packages of their own — they ship inside
`Cairn.AspNetCore` under `analyzers/dotnet/cs`.

## Releases and versioning

Releases are tag-driven via MinVer — see [RELEASING.md](RELEASING.md). There is
no version number to bump in a PR.

## Decisions of record

### No strong naming

Cairn assemblies are deliberately **not** strong-named, and PRs adding strong
naming will be declined. Strong naming is a legacy .NET Framework concern: on
modern .NET (Core/5+) it provides no security and no loading benefit, while
imposing real costs — binding redirect friction for .NET Framework consumers,
the private key either being public anyway (making the "identity" meaningless)
or a signing bottleneck, and the inability to unify with non-strong-named
dependencies. Strong names are [not a security feature](https://github.com/dotnet/runtime/blob/main/docs/project/strong-name-signing.md),
and much of the modern OSS .NET ecosystem ships without them. Should a future
consumer scenario genuinely require it, adding a strong name is a
binary-breaking change and would be a major-version decision.

## Reporting security issues

Please do not open public issues for vulnerabilities — see
[SECURITY.md](SECURITY.md) for private reporting.
