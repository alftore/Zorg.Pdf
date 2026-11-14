#!/usr/bin/env bash
# Runs inside the gate container (see test-linux.sh). Reads the source tree as a tar stream on stdin, builds, tests,
# runs the smoke sample, checks every PDF it writes, and leaves the PDFs in /out for veraPDF.
set -euo pipefail

stage() { printf '\n== %s (%s)\n' "$1" "$(date +%H:%M:%S)"; }

stage "Unpack"
tar -xf - -C /w
cd /w

stage "Build"
timeout 900 dotnet build Zorg.Pdf.slnx -c Release -v q -nologo

stage "Test (fonts and oracles required: ZORG_REQUIRE_FONTS=$ZORG_REQUIRE_FONTS, ZORG_REQUIRE_ORACLES=$ZORG_REQUIRE_ORACLES)"
timeout 900 dotnet test --solution Zorg.Pdf.slnx -c Release --no-build

stage "Smoke sample"
mkdir -p /out
rm -f /out/*.pdf # The volume outlives the container: start clean.
timeout 300 dotnet run --project samples/Zorg.Pdf.AotSmoke -c Release --no-build -- /out

stage "Validate documents against the JSON Schema (python-jsonschema)"
python3 - schema/document-0.1.schema.json docs/examples/*.json /out/*.json <<'PY' || exit 1
import json, sys
from jsonschema import Draft202012Validator
schema = json.load(open(sys.argv[1], encoding="utf-8"))
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema)
failed = False
for path in sys.argv[2:]:
    errors = sorted(validator.iter_errors(json.load(open(path, encoding="utf-8"))), key=lambda e: list(e.absolute_path))
    for error in errors:
        print(f"FAIL {path} at /{'/'.join(map(str, error.absolute_path))}: {error.message[:200]}")
    failed |= bool(errors)
    if not errors:
        print(f"ok   {path}")
# The schema must also reject what the parser rejects, or agents get no warning from it.
for bad in [
    {"title": "t", "colour": "red"},
    {"title": "t", "content": [{"type": "paragraph"}]},
    {"title": "t", "content": [{"type": "chart"}]},
    {"title": "t", "styles": {"styles": {"x": {"color": "red"}}}},
    {"title": "t", "content": [{"type": "table", "columns": [{"header": "A"}], "rows": [[{"text": "x"}]]}]},
    {"content": []},
]:
    if validator.is_valid(bad):
        print(f"FAIL the schema accepts an invalid document: {json.dumps(bad)}")
        failed = True
if not failed:
    print("ok   the schema rejects six invalid documents")
sys.exit(1 if failed else 0)
PY

stage "Check every PDF"
failed=0
for pdf in /out/*.pdf; do
  name="$(basename "$pdf")"
  if ! qpdf --check "$pdf" >/tmp/qpdf.txt 2>&1; then
    echo "FAIL qpdf $name"; cat /tmp/qpdf.txt; failed=1; continue
  fi

  # Render every page; mutool reports content stream errors on stderr.
  if mutool draw -q -o /dev/null "$pdf" 2>&1 | grep -q .; then
    echo "FAIL mutool $name"; mutool draw -q -o /dev/null "$pdf"; failed=1; continue
  fi

  # Every font must be embedded, and every embedded TrueType subset and mapped to Unicode.
  if pdffonts "$pdf" | awk 'NR > 2 && $0 ~ /CID TrueType/ && !($(NF-4) == "yes" && $(NF-3) == "yes" && $(NF-2) == "yes")' | grep -q .; then
    echo "FAIL pdffonts $name"; pdffonts "$pdf"; failed=1; continue
  fi

  echo "ok   $name ($(pdfinfo "$pdf" | awk '/^Pages:/ { print $2 }') pages)"
done

# Text must come back out, Norwegian letters included. Written to a file first: with pipefail, "pdftotext | grep -q"
# fails when grep stops reading early and pdftotext gets SIGPIPE.
pdftotext -enc UTF-8 /out/invoice.pdf /tmp/invoice.txt
if ! grep -q "Blåbærsyltetøy-integrasjon" /tmp/invoice.txt; then
  echo "FAIL pdftotext invoice.pdf: the Norwegian text doesn't extract"; failed=1
fi
# The footer's fields: the first and last page say "Page n of N" with the real count.
pages="$(pdfinfo /out/invoice.pdf | awk '/^Pages:/ { print $2 }')"
for page in 1 "$pages"; do
  pdftotext -enc UTF-8 -f "$page" -l "$page" /out/invoice.pdf /tmp/page.txt
  if ! grep -q "Page $page of $pages" /tmp/page.txt; then
    echo "FAIL pdftotext invoice.pdf: page $page doesn't say \"Page $page of $pages\""; failed=1
  fi
done
pdftotext -enc UTF-8 /out/document.pdf /tmp/document.txt
if ! grep -q "Ελληνικά · Кириллица · Čeština" /tmp/document.txt; then
  echo "FAIL pdftotext document.pdf: the Greek, Cyrillic and Czech text doesn't extract"; failed=1
fi

stage "Previews against MuPDF"
# Zorg.Pdf.Imaging's preview of each example's first page, against mutool's drawing of the same page at 96 dpi:
# the total ink must agree within 3 %, and the ink of every 16 x 16 block within a quarter of the block's range.
for png in /out/example-*.png; do
  pdf="${png%.png}.pdf"
  mutool draw -q -r 96 -o /tmp/reference.png "$pdf" 1
  python3 - "$png" /tmp/reference.png <<'PY' || failed=1
import sys
from PIL import Image
ours, theirs = (Image.open(p).convert("L") for p in sys.argv[1:3])
w, h = min(ours.width, theirs.width), min(ours.height, theirs.height)
a, b = ours.crop((0, 0, w, h)).load(), theirs.crop((0, 0, w, h)).load()
ink_a = sum(255 - a[x, y] for y in range(h) for x in range(w))
ink_b = sum(255 - b[x, y] for y in range(h) for x in range(w))
total = abs(ink_a - ink_b) / max(ink_b, 1)
worst = 0.0
for by in range(0, h - 15, 16):
    for bx in range(0, w - 15, 16):
        d = abs(sum(a[x, y] - b[x, y] for y in range(by, by + 16) for x in range(bx, bx + 16))) / 256
        worst = max(worst, d)
name = sys.argv[1].rsplit("/", 1)[-1]
size = f"{ours.width}x{ours.height} vs {theirs.width}x{theirs.height}"
if total <= 0.03 and worst <= 64 and abs(ours.width - theirs.width) <= 1 and abs(ours.height - theirs.height) <= 1:
    print(f"ok   {name}: ink within {total:.2%}, worst 16px block {worst:.1f} levels ({size})")
else:
    print(f"FAIL {name}: ink differs {total:.2%}, worst 16px block {worst:.1f} levels ({size})")
    sys.exit(1)
PY
done

exit "$failed"
