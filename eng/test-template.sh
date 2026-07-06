#!/usr/bin/env bash
#
# Generate-and-build smoke test for the `dotnet new cairn-api` template.
#
# Installs the freshly packed Cairn.Templates package, scaffolds a project for a representative
# matrix of options and target frameworks, and builds each one against the freshly packed Cairn
# libraries (from the same artifacts/ feed). This keeps the template from silently rotting when the
# library APIs change: if a generated project no longer compiles, this fails.
#
# Usage: eng/test-template.sh [artifacts-dir]   (default: artifacts)
set -euo pipefail

FEED="$(cd "${1:-artifacts}" && pwd)"

# nullglob so an unmatched pattern expands to nothing (not the literal glob), which keeps the
# guard below reachable under `set -euo pipefail` — `ls ... | head` would abort the script on the
# failed glob before the guard, or SIGPIPE-fail if the feed held more than one match.
shopt -s nullglob
template_nupkgs=("$FEED"/Cairn.Templates.*.nupkg)
shopt -u nullglob   # scope nullglob to the glob above; leave later expansions (e.g. $args) unaffected
TEMPLATE_NUPKG="${template_nupkgs[0]:-}"

if [ -z "$TEMPLATE_NUPKG" ]; then
  echo "No Cairn.Templates.*.nupkg found in $FEED" >&2
  exit 1
fi
echo "Template package: $TEMPLATE_NUPKG"
echo "Local feed:       $FEED"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; dotnet new uninstall Cairn.Templates >/dev/null 2>&1 || true' EXIT

# Resolve generated projects against the local feed (for the just-built Cairn packages) and
# nuget.org (for third-party dependencies such as Swashbuckle).
cat > "$WORK/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

dotnet new uninstall Cairn.Templates >/dev/null 2>&1 || true
dotnet new install "$TEMPLATE_NUPKG"

# name | template arguments
combos=(
  "DefaultNet10|-f net10.0"
  "FullNet10|-f net10.0 --explorer true --openapi true"
  "MinimalNet9|-f net9.0 --explorer false --openapi false"
  "FullNet8|-f net8.0 --explorer true --openapi true"
)

failed=0
for c in "${combos[@]}"; do
  name="${c%%|*}"; args="${c#*|}"
  echo
  echo "=== $name ($args) ==="
  out="$WORK/$name"
  dotnet new cairn-api -o "$out" $args
  cp "$WORK/nuget.config" "$out/nuget.config"
  if dotnet build "$out" -c Release; then
    echo "PASS: $name"
  else
    echo "FAIL: $name"
    failed=1
  fi
done

if [ "$failed" -ne 0 ]; then
  echo "One or more generated projects failed to build." >&2
  exit 1
fi
echo
echo "All generated projects built successfully."
