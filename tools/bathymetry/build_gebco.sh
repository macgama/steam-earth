#!/usr/bin/env bash
# Télécharge la grille GEBCO (GeoTIFF, ~4 Go) puis la découpe en COG (build_gebco_cogs.py).
# Lancé par .github/workflows/gebco.yml : le site de GEBCO n'est pas joignable depuis tous les environnements,
# mais l'est depuis les machines de GitHub Actions.
# Usage : build_gebco.sh <année> <dossier de sortie>
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
YEAR="${1:-2025}"
OUT="$(realpath -m "${2:-out}")"
WORK="${WORK:-$PWD/gebco_work}"
mkdir -p "$WORK" "$OUT"
cd "$WORK"
ok=""
for y in "$YEAR" $((YEAR - 1)); do
  for url in "https://www.bodc.ac.uk/data/open_download/gebco/gebco_${y}/geotiff/" \
             "https://www.bodc.ac.uk/data/open_download/gebco/gebco_${y}/zip/"; do
    echo "Essai : $url"
    if curl -fL --retry 3 --retry-delay 10 -o gebco.zip "$url" && unzip -l gebco.zip >/dev/null 2>&1; then
      ok="$y"; break 2
    fi
    rm -f gebco.zip
  done
done
[ -n "$ok" ] || { echo "Grille GEBCO introuvable"; exit 1; }
echo "GEBCO $ok téléchargé ($(du -h gebco.zip | cut -f1))"
echo "$ok" > "$WORK/year"
unzip -l gebco.zip
unzip -o gebco.zip '*.tif' '*.nc' -d grid || true
rm -f gebco.zip
mapfile -t TIFS < <(find grid -name '*.tif' | sort)
if [ "${#TIFS[@]}" -gt 0 ]; then
  gdalbuildvrt grid/gebco.vrt "${TIFS[@]}"
  SRC="$WORK/grid/gebco.vrt"
else
  NC=$(find grid -name '*.nc' | head -1)
  SRC="NETCDF:\"$WORK/$NC\":elevation"
fi
python3 "$HERE/build_gebco_cogs.py" "$SRC" "$OUT"
python3 - "$OUT/gebco_info.json" "$ok" <<'PY'
import json, sys
p, y = sys.argv[1], sys.argv[2]
d = json.load(open(p)); d["year"] = int(y)
d["attribution"] = f"GEBCO Compilation Group ({y}) GEBCO {y} Grid"
json.dump(d, open(p, "w"), indent=1)
PY
