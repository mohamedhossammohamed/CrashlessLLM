#!/usr/bin/env bash
set -euo pipefail

# Publish CrashlessLLM NuGet package.
#
# Prerequisites:
#   1. Set NUGET_API_KEY environment variable (or pass --api-key).
#   2. Build native binaries for all target RIDs and place them under runtimes/.
#   3. Run this script from the repo root.
#
# Usage:
#   ./scripts/publish-nuget.sh                          # push to nuget.org
#   ./scripts/publish-nuget.sh --source <url>           # push to custom feed
#   ./scripts/publish-nuget.sh --dry-run                # pack only, don't push

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

SOURCE="https://api.nuget.org/v3/index.json"
DRY_RUN=false
API_KEY="${NUGET_API_KEY:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)
            SOURCE="$2"; shift 2 ;;
        --api-key)
            API_KEY="$2"; shift 2 ;;
        --dry-run)
            DRY_RUN=true; shift ;;
        *)
            echo "Unknown argument: $1"
            exit 1
            ;;
    esac
done

echo "=== Building Release configuration ==="
dotnet build "$REPO_ROOT/CrashlessLLM.sln" --configuration Release

echo "=== Running stress tests ==="
test_model="${RUNNER_TEMP:-/tmp}/crashless-publish-model.gguf"
printf "ci" > "$test_model"
CRASHLESS_TEST_MODEL_PATH="$test_model" \
    dotnet test "$REPO_ROOT/CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj" \
    --configuration Release --framework net9.0

echo "=== Packing NuGet package ==="
dotnet pack "$REPO_ROOT/src/CrashlessLLM/CrashlessLLM.csproj" --configuration Release --no-build

PKG=$(find "$REPO_ROOT/src/CrashlessLLM/bin/Release" -name '*.nupkg' | head -n 1)
if [ -z "$PKG" ]; then
    echo "ERROR: No .nupkg produced."
    exit 1
fi

echo "=== Package: $PKG ==="
echo ""
echo "Contents:"
unzip -l "$PKG" | grep -E '(\.dll|\.dylib|\.so|README|LICENSE|nuspec)' || true
echo ""

if [ "$DRY_RUN" = true ]; then
    echo "Dry run complete. Package at: $PKG"
    exit 0
fi

if [ -z "$API_KEY" ]; then
    echo "ERROR: NUGET_API_KEY environment variable not set. Use --api-key or set the env var."
    exit 1
fi

echo "=== Pushing to $SOURCE ==="
dotnet nuget push "$PKG" --api-key "$API_KEY" --source "$SOURCE"

echo "=== Published successfully ==="