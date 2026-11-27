#!/usr/bin/env bash
# Day-of-demo helper for the one-command deploy (deploy/acme-insurance.yaml).
# Runs against whatever project you are logged in to. Needs only oc; jq and
# curl are used by "status" if present.
#
#   scripts/demo.sh status              which build each service runs, and how old its base is
#   scripts/demo.sh pull                fetch the newest builds from Quay now and roll them out
#   scripts/demo.sh switch hardened     move both services to the Hardened Images build
#   scripts/demo.sh switch ubi          ...and back to UBI
#
# Why not "oc set image"? Each Deployment follows an ImageStream tag, so the
# cluster would put the old image straight back. Switching means pointing the
# Deployment at a different tag, which is what "switch" does.
set -euo pipefail

SERVICES=(web quote-api)
STREAM=acme-insurance

die() { echo "$*" >&2; exit 1; }
command -v oc >/dev/null || die "oc is required"

# Which build (ubi or hardened) a Deployment currently follows.
following() {
  oc get "deployment/$1" -o jsonpath='{.metadata.annotations.image\.openshift\.io/triggers}' \
    | sed -nE 's/.*"'"${STREAM}"':'"$1"'-(ubi|hardened)-latest".*/\1/p'
}

trigger() {  # $1 service, $2 base
  printf '[{"from":{"kind":"ImageStreamTag","name":"%s:%s-%s-latest"},"fieldPath":"spec.template.spec.containers[?(@.name==\\"%s\\")].image"}]' \
    "${STREAM}" "$1" "$2" "$1"
}

wait_for_rollout() {
  for s in "${SERVICES[@]}"; do oc rollout status "deployment/$s" --timeout=180s; done
}

status() {
  for s in "${SERVICES[@]}"; do
    printf '%-10s follows %-9s running %s\n' "$s" "$(following "$s")" \
      "$(oc get "deployment/$s" -o jsonpath='{.spec.template.spec.containers[0].image}')"
  done
  local host
  host=$(oc get route acme-insurance -o jsonpath='{.spec.host}' 2>/dev/null || true)
  if [[ -n "$host" ]] && command -v curl >/dev/null && command -v jq >/dev/null; then
    echo
    curl -fsS "https://${host}/api/info" \
      | jq -r '.web, .api | "\(.service): \(.baseImage), base image built \(.baseCreated // "unknown")"' \
      || echo "(the site did not answer)"
    echo
    echo "https://${host}"
  fi
}

case "${1:-status}" in
  status)
    status ;;
  pull)
    # Re-check Quay for the tags in use. A new digest rolls out by itself.
    for s in "${SERVICES[@]}"; do
      b=$(following "$s"); [[ -n "$b" ]] || die "deployment/$s has no image trigger; was it deployed from deploy/acme-insurance.yaml?"
      oc import-image "${STREAM}:${s}-${b}-latest" >/dev/null
      echo "checked Quay for ${s}-${b}-latest"
    done
    wait_for_rollout ;;
  switch)
    base="${2:-}"
    [[ "$base" == ubi || "$base" == hardened ]] || die "usage: $0 switch ubi|hardened"
    for s in "${SERVICES[@]}"; do
      oc annotate "deployment/$s" --overwrite "image.openshift.io/triggers=$(trigger "$s" "$base")" >/dev/null
      echo "$s now follows ${s}-${base}-latest"
    done
    wait_for_rollout ;;
  *)
    sed -n '2,13p' "$0"; exit 1 ;;
esac
