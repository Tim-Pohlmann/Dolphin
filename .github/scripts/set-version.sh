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
  tmp=$(mktemp)
  jq --arg ref "$stable_ref" '.plugins[0].source.ref = $ref' .claude-plugin/marketplace.json > "$tmp" && mv "$tmp" .claude-plugin/marketplace.json
  marketplace_ref="$(jq -r '.plugins[0].source.ref' .claude-plugin/marketplace.json 2>/dev/null || true)"
  if [[ "$marketplace_ref" != "$stable_ref" ]]; then
    echo "Error: failed to update .claude-plugin/marketplace.json ref to $stable_ref (found: ${marketplace_ref:-<missing>})" >&2
    exit 1
  fi
fi
