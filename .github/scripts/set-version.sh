#!/usr/bin/env bash
set -euo pipefail
version="$1"
stable_ref="${2:-}"

sed -i "s/\"version\": \"[^\"]*\"/\"version\": \"$version\"/" .claude-plugin/plugin.json
plugin_version="$(jq -r '.version' .claude-plugin/plugin.json 2>/dev/null || true)"
if [[ "$plugin_version" != "$version" ]]; then
  echo "Error: failed to update .claude-plugin/plugin.json version to $version (found: ${plugin_version:-<missing>})" >&2
  exit 1
fi

sed -i "s|<Version>[^<]*</Version>|<Version>$version</Version>|" src/Dolphin/Dolphin.csproj
csproj_version="$(grep -oPm1 '(?<=<Version>)[^<]+' src/Dolphin/Dolphin.csproj || true)"
if [[ "$csproj_version" != "$version" ]]; then
  echo "Error: failed to update src/Dolphin/Dolphin.csproj version to $version (found: ${csproj_version:-<missing>})" >&2
  exit 1
fi

if [[ -n "$stable_ref" ]]; then
  sed -i "s/\"ref\": \"[^\"]*\"/\"ref\": \"$stable_ref\"/" .claude-plugin/marketplace.json
fi
