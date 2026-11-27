#!/usr/bin/env bash
# Pin every base image in the Containerfiles to its current digest:
#
#   FROM registry.access.redhat.com/hi/nodejs:24
# becomes
#   FROM registry.access.redhat.com/hi/nodejs:24@sha256:<digest>
#
# Run it once and commit the result. After that, Dependabot opens a pull
# request whenever a base image is rebuilt, because the digest has moved.
# Lines that already carry a digest are left alone.
#
# Needs skopeo. Runs rootless and changes only files in this repository.
set -euo pipefail

cd "$(dirname "$0")/.."
command -v skopeo >/dev/null || { echo "skopeo is required" >&2; exit 1; }

changed=0
for file in services/*/Containerfile*; do
  while IFS= read -r image; do
    [[ "$image" == *@sha256:* ]] && continue
    digest=$(skopeo inspect --format '{{.Digest}}' "docker://${image}")
    [[ "$digest" == sha256:* ]] || { echo "no digest for ${image}" >&2; exit 1; }
    escaped=$(printf '%s' "$image" | sed 's|[.[*^$]|\\&|g')
    sed -i.bak -E "s|^(FROM[[:space:]]+)${escaped}([[:space:]]\|\$)|\1${image}@${digest}\2|" "$file"
    rm -f "${file}.bak"
    echo "${file}: ${image} -> ${digest}"
    changed=1
  done < <(grep -E '^FROM[[:space:]]' "$file" | awk '{print $2}' | sort -u)
done

[[ "$changed" == 1 ]] && echo "Done. Review with 'git diff' and commit." || echo "Everything is already pinned."
