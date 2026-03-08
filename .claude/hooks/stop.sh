#!/bin/bash
# Fetch SonarCloud PR analysis results after each Claude turn (Stop hook).
# Reads from GitHub PR issue comments posted by the sonarqubecloud[bot] —
# no SonarCloud credentials needed for public repos.
set -euo pipefail

# Only run in remote (cloud) sessions
if [[ "${CLAUDE_CODE_REMOTE:-}" != "true" ]]; then
  exit 0
fi

OUTPUT_FILE="${CLAUDE_PROJECT_DIR:-.}/.sonarqube-pr-results.md"
REPO="Tim-Pohlmann/Dolphin"
GH_API="https://api.github.com/repos/${REPO}"
GH_HEADERS=(-H "Accept: application/vnd.github.v3+json")

# Find the PR number for the current branch via the GitHub API
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || true)
if [[ -z "$BRANCH" || "$BRANCH" == "HEAD" ]]; then
  echo "[sonarqube-hook] Could not determine current branch — skipping." >&2
  exit 0
fi

PR_JSON=$(curl -sf "${GH_HEADERS[@]}" \
  "${GH_API}/pulls?state=open&head=${REPO%%/*}:${BRANCH}&per_page=1" \
  2>/dev/null || echo '[]')
PR_NUMBER=$(echo "$PR_JSON" | jq -r '.[0].number // empty')

if [[ -z "$PR_NUMBER" ]]; then
  echo "[sonarqube-hook] No open PR found for branch '${BRANCH}' — skipping." >&2
  rm -f "$OUTPUT_FILE"
  exit 0
fi

# ── SonarCloud bot comment (primary strategy) ─────────────────────────────────
# SonarCloud posts its quality gate summary as a PR issue comment from
# sonarqubecloud[bot]. Fetch the most recent one.

SONAR_BODY=$(curl -sf "${GH_HEADERS[@]}" \
  "${GH_API}/issues/${PR_NUMBER}/comments?per_page=100" \
  2>/dev/null \
  | jq -r '
      [ .[]? | select(.user.login | ascii_downcase | contains("sonar")) ]
      | last
      | .body // empty
    ' 2>/dev/null || true)

if [[ -n "$SONAR_BODY" ]]; then
  {
    echo "# SonarCloud PR #${PR_NUMBER} Results"
    echo ""
    echo "$SONAR_BODY"
    echo ""
    echo "_Generated at $(date -u '+%Y-%m-%dT%H:%M:%SZ')_"
  } > "$OUTPUT_FILE"

  echo "[sonarqube-hook] Wrote SonarCloud results for PR #${PR_NUMBER} (via GitHub PR comment)" >&2
  exit 0
fi

echo "[sonarqube-hook] No SonarCloud comment found on PR #${PR_NUMBER} — skipping." >&2
rm -f "$OUTPUT_FILE"
