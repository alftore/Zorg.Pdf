#!/usr/bin/env bash
# The Linux gate: build and test Zorg.Pdf in an Alpine container with every oracle tool, check the PDFs the smoke
# sample writes (qpdf, mutool, pdffonts, pdftotext), and validate the tagged ones with veraPDF (PDF/UA-1).
#
#   scripts/test-linux.sh [--no-oracles] [--keep DIR]
#
#   --no-oracles  Don't set ZORG_REQUIRE_ORACLES=1. By default a missing oracle tool fails a test instead of skipping.
#   --keep DIR    Copy the PDFs the smoke sample wrote to DIR, to look at them.
#
# Modelled on Zorg.Imaging's gate. The tree goes in as a tar stream of the files git knows (tracked and untracked,
# not ignored), uncommitted edits included. Nothing is bind-mounted from the host. NuGet packages are cached in the
# named volume zorg-pdf-nuget, and the PDFs pass through zorg-pdf-out. Every step has a timeout. Set ZORG_PDF_CONTAINER_CLI to force docker or podman.
set -euo pipefail

oracles=1
keep=
while [ $# -gt 0 ]; do
  case "$1" in
    --no-oracles) oracles=0 ;;
    --keep) keep="$2"; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

# Git Bash on Windows rewrites arguments that look like POSIX paths; container paths must pass through unchanged.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

find_cli() {
  if [ -n "${ZORG_PDF_CONTAINER_CLI:-}" ]; then echo "$ZORG_PDF_CONTAINER_CLI"; return; fi
  for name in docker podman; do
    if command -v "$name" >/dev/null 2>&1; then command -v "$name"; return; fi
  done
  local known="${LOCALAPPDATA:-}/Programs/Podman/podman.exe"
  if [ -n "${LOCALAPPDATA:-}" ] && [ -x "$known" ]; then echo "$known"; return; fi
  echo "No docker or podman found. Set ZORG_PDF_CONTAINER_CLI." >&2
  exit 1
}
cli="$(find_cli)"
image=zorg-pdf-gate:alpine
verapdf=docker.io/verapdf/cli@sha256:82a5e28f8ac21210f6eef12a51fd96b240ee1eb0d0223574864cc4d13db3f929 # veraPDF 1.30.4
volume=zorg-pdf-nuget
outvolume=zorg-pdf-out
name="zorg-pdf-gate-$$"
out="$(mktemp -d)"

stage() { printf '\n== %s (%s)\n' "$1" "$(date +%H:%M:%S)"; }
run_cli() { local limit="$1"; shift; timeout "$limit" "$cli" "$@"; }
cleanup() { run_cli 60 rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$out"; }
trap cleanup EXIT

# Host networking on purpose: on the Podman machine used for development, the default rootless network was slow
# enough to make package installs look hung (the same finding as in Zorg.Ssh and Zorg.Imaging).
stage "Image $image"
run_cli 1200 build --network host -t "$image" -f scripts/linux/Dockerfile.alpine scripts/linux

stage "Build, test and check in the container"
failed=0
git ls-files -z --cached --others --exclude-standard | tar --null -T - -cf - |
  run_cli 2400 run -i --name "$name" --network host -v "$volume:/root/.nuget/packages" \
    -v "$outvolume:/out" -e "ZORG_REQUIRE_ORACLES=$oracles" "$image" gate-inner.sh || failed=1

# The PDFs are in the volume zorg-pdf-out. A second container streams them out as a tar on stdout, so no host path
# is involved (podman.exe on Windows can't see Git Bash's /tmp, and its "cp" can't write to stdout). They're copied
# even after a failure, to look at.
run_cli 120 run --rm -v "$outvolume:/out" "$image" tar -cf - -C /out . | tar -xf - -C "$out" ||
  { echo "FAIL no PDFs came out of the container"; exit 1; }

stage "veraPDF $verapdf"
for pdf in invoice.pdf; do
  report="$(run_cli 300 run -i --rm --entrypoint sh "$verapdf" -c \
    'cat > /tmp/f.pdf && /opt/verapdf/verapdf --flavour ua1 --format xml /tmp/f.pdf 2>/dev/null' < "$out/$pdf")"
  # Here-strings, not pipes: with pipefail, "printf | grep -q" can fail when grep stops reading early.
  if grep -q 'isCompliant="true"' <<<"$report"; then
    echo "ok   $pdf is PDF/UA-1 compliant"
  else
    echo "FAIL $pdf is not PDF/UA-1 compliant:"
    grep -E '<rule |<description>' <<<"$report" | head -40 || true
    failed=1
  fi
done

if [ -n "$keep" ]; then
  mkdir -p "$keep"
  cp "$out"/*.pdf "$keep"/
  echo "PDFs copied to $keep"
fi

exit "$failed"
