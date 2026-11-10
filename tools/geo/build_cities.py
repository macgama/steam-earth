"""Liste des villes du monde pour la recherche du menu de départ.

Source : Natural Earth 1:10m populated places (domaine public), ~7 300 villes.
Sortie : TSV gzip « nom<TAB>pays<TAB>lat<TAB>lon<TAB>population », noms en français quand ils existent,
pays avec le nom français de data/geo/countries.json (code ISO), triées par population décroissante.

    curl -LO https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_populated_places.geojson
    python build_cities.py ne_10m_populated_places.geojson ../../data/geo/cities.tsv.gz
"""
import gzip, json, sys

src, dst = sys.argv[1], sys.argv[2]
here = __file__.rsplit('/', 1)[0] if '/' in __file__ else '.'
countries = json.load(open(f"{here}/../../data/geo/countries.json", encoding="utf-8"))
by_iso = {c["iso"]: c["name"] for c in countries if c.get("iso")}
rows = []
for f in json.load(open(src, encoding="utf-8"))["features"]:
    p = f["properties"]
    name = (p.get("NAME_FR") or p.get("NAME") or "").strip()
    if not name:
        continue
    country = by_iso.get(p.get("ISO_A2")) or p.get("ADM0NAME") or ""
    pop = int(p.get("POP_MAX") or 0)
    rows.append((pop, name, country, p["LATITUDE"], p["LONGITUDE"]))
rows.sort(key=lambda r: -r[0])
with gzip.open(dst, "wt", encoding="utf-8") as out:
    for pop, name, country, lat, lon in rows:
        out.write(f"{name}\t{country}\t{lat:.5f}\t{lon:.5f}\t{pop}\n")
print(len(rows), "villes")
