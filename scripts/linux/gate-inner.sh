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

exit "$failed"
