#!/bin/bash
# Fetch SonarQube PR analysis results and write a summary for Claude to reference.
# Requires: SONARQUBE_URL, SONARQUBE_TOKEN, SONARQUBE_PROJECT_KEY env vars.
# Skips gracefully if any are missing or no PR is open for the current branch.
set -euo pipefail

# Only run in remote (cloud) sessions
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

OUTPUT_FILE="${CLAUDE_PROJECT_DIR:-.}/.sonarqube-pr-results.md"

# Check required env vars
if [ -z "${SONARQUBE_URL:-}" ] || [ -z "${SONARQUBE_TOKEN:-}" ] || [ -z "${SONARQUBE_PROJECT_KEY:-}" ]; then
  echo "[sonarqube-hook] SONARQUBE_URL, SONARQUBE_TOKEN, or SONARQUBE_PROJECT_KEY not set — skipping." >&2
  exit 0
fi

# Find the PR number for the current branch
PR_NUMBER=$(gh pr view --json number --jq '.number' 2>/dev/null || true)
if [ -z "$PR_NUMBER" ]; then
  echo "[sonarqube-hook] No open PR found for current branch — skipping." >&2
  rm -f "$OUTPUT_FILE"
  exit 0
fi

BASE_URL="${SONARQUBE_URL%/}"
AUTH_HEADER="Authorization: Bearer ${SONARQUBE_TOKEN}"

# Fetch quality gate status
QG_JSON=$(curl -sf -H "$AUTH_HEADER" \
  "${BASE_URL}/api/qualitygates/project_status?pullRequest=${PR_NUMBER}&projectKey=${SONARQUBE_PROJECT_KEY}" \
  2>/dev/null || echo '{}')

QG_STATUS=$(echo "$QG_JSON" | jq -r '.projectStatus.status // "UNKNOWN"')

# Fetch issues (up to 50, ERROR and WARN severity)
ISSUES_JSON=$(curl -sf -H "$AUTH_HEADER" \
  "${BASE_URL}/api/issues/search?pullRequest=${PR_NUMBER}&projectKeys=${SONARQUBE_PROJECT_KEY}&ps=50&resolved=false" \
  2>/dev/null || echo '{"issues":[],"total":0}')

TOTAL=$(echo "$ISSUES_JSON" | jq -r '.total // 0')

# Write markdown summary
{
  echo "# SonarQube PR #${PR_NUMBER} Results"
  echo ""

  # Quality gate badge
  if [ "$QG_STATUS" = "OK" ]; then
    echo "**Quality Gate: PASSED**"
  elif [ "$QG_STATUS" = "ERROR" ]; then
    echo "**Quality Gate: FAILED**"
  else
    echo "**Quality Gate: ${QG_STATUS}**"
  fi

  echo ""
  echo "**Total issues:** ${TOTAL}"
  echo ""

  # Conditions (what caused gate to fail/pass)
  CONDITIONS=$(echo "$QG_JSON" | jq -r '
    .projectStatus.conditions[]?
    | "- \(.metricKey): \(.status) (actual: \(.actualValue // "n/a"), threshold: \(.errorThreshold // "n/a"))"
  ' 2>/dev/null || true)

  if [ -n "$CONDITIONS" ]; then
    echo "## Quality Gate Conditions"
    echo ""
    echo "$CONDITIONS"
    echo ""
  fi

  # Issues list
  if [ "$TOTAL" -gt 0 ]; then
    echo "## Issues"
    echo ""
    echo "$ISSUES_JSON" | jq -r '
      .issues[]
      | "### \(.severity // "UNKNOWN") — \(.rule // "")\n**\(.message)**\n\(.component // ""):\(.textRange.startLine // "?")\n"
    ' 2>/dev/null || true
  fi

  echo "_Generated at $(date -u '+%Y-%m-%dT%H:%M:%SZ')_"
} > "$OUTPUT_FILE"

echo "[sonarqube-hook] Wrote SonarQube PR #${PR_NUMBER} results to ${OUTPUT_FILE}" >&2
