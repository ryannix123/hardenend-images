#!/usr/bin/env bash
# Set up the "before" picture for the remediation demo: pin a service's UBI
# runtime image to an OLDER real Red Hat build, so the app runs with known,
# already-fixed vulnerabilities. Dependabot then opens a pull request to move
# it to the current build, and that pull request is the fix you merge live.
#
#   scripts/time-machine.sh list web            recent builds of web's UBI base, with dates
#   scripts/time-machine.sh pin web 120         pin to the newest build at least 120 days old
#   scripts/time-machine.sh pin web <tag>       pin to an exact tag from the list
#
# Only the UBI Containerfile changes; Containerfile.hardened is left current.
# Nothing is invented: every candidate is a real tag in Red Hat's registry, and
# the pull request comment from the pipeline shows exactly which CVEs it fixes.
#
# Needs skopeo and jq. Runs rootless and changes only files in this repository.
set -euo pipefail

cd "$(dirname "$0")/.."
die() { echo "$*" >&2; exit 1; }
for t in skopeo jq; do command -v "$t" >/dev/null || die "$t is required"; done

cmd="${1:-}"; svc="${2:-}"
[[ "$cmd" == list || "$cmd" == pin ]] && [[ -n "$svc" ]] || { sed -n '2,15p' "$0"; exit 1; }
file="services/${svc}/Containerfile"
[[ -f "$file" ]] || die "no such service: ${svc} (expected ${file})"

# The runtime image is the last FROM line. Strip any tag and digest to get the repository.
line=$(grep -nE '^FROM[[:space:]]' "$file" | tail -1)
lineno=${line%%:*}
image=$(awk '{print $2}' <<<"${line#*:}")
repo=${image%%@*}
[[ "${repo##*/}" == *:* ]] && repo=${repo%:*}

# Red Hat build tags look like 10.1-1763 or 9.6-1747219013 or 1-59.1726663413.
# Take the most recent 30 by version, then ask the registry when each was built.
candidates() {
  skopeo list-tags "docker://${repo}" \
    | jq -r '.Tags[]' \
    | grep -E '^[0-9][0-9.]*-[0-9][0-9.]*$' \
    | sort -V | tail -30
}

builds() {  # prints: created<TAB>tag<TAB>digest, oldest first
  local tag info
  while IFS= read -r tag; do
    info=$(skopeo inspect --no-tags --format '{{.Created}}	{{.Digest}}' "docker://${repo}:${tag}" 2>/dev/null) || continue
    printf '%s\t%s\t%s\n' "${info%%	*}" "$tag" "${info#*	}"
  done < <(candidates) | sort
}

case "$cmd" in
  list)
    echo "Recent builds of ${repo} (now in ${file}: ${image})"
    builds | awk -F'\t' '{printf "  %s  %s\n", substr($1,1,10), $2}'
    ;;
  pin)
    target="${3:?usage: $0 pin <service> <days-old|tag>}"
    all=$(builds)
    [[ -n "$all" ]] || die "no build tags found for ${repo}"
    if [[ "$target" =~ ^[0-9]+$ ]]; then
      cutoff=$(date -u -d "-${target} days" +%Y-%m-%dT%H:%M:%S 2>/dev/null \
               || date -u -v-"${target}"d +%Y-%m-%dT%H:%M:%S)   # GNU date, then macOS
      pick=$(awk -F'\t' -v c="$cutoff" '$1 <= c' <<<"$all" | tail -1)
      [[ -n "$pick" ]] || die "no build of ${repo} is ${target}+ days old among the last 30; try a smaller number"
    else
      pick=$(awk -F'\t' -v t="$target" '$2 == t' <<<"$all")
      [[ -n "$pick" ]] || die "tag ${target} not found; run: $0 list ${svc}"
    fi
    IFS=$'\t' read -r created tag digest <<<"$pick"
    newest=$(tail -1 <<<"$all" | cut -f1)
    # Keep any "AS <stage>" suffix on the FROM line.
    sed -i.bak -E "${lineno}s|^(FROM[[:space:]]+)[^[:space:]]+|\1${repo}:${tag}@${digest}|" "$file"
    rm -f "${file}.bak"
    echo "${file}: runtime base pinned to ${repo}:${tag}"
    echo "  built ${created%%T*}; the newest build is from ${newest%%T*}"
    echo
    echo "Next: commit and push. The pipeline rebuilds ${svc} on the old base and"
    echo "Dependabot will offer the fix as a pull request (Insights > Dependency graph >"
    echo "Dependabot > Check for updates, to get it now)."
    ;;
esac
