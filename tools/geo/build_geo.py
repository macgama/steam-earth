"""Frontières terrestres des pays et capitales, depuis Natural Earth (domaine public).
https://www.naturalearthdata.com — fichiers GeoJSON du dépôt nvkelso/natural-earth-vector.

Usage : python build_geo.py DOSSIER_GEOJSON ../../data/geo
Produit :
  countries.json   : [{name, capital, lat, lon, iso}] trié par nom français
  borders.bin.gz   : 'SEBL', int32 nb de lignes, puis pour chaque ligne int32 nb de points + float32 (lat, lon)…
"""
import gzip, json, os, struct, sys, locale

src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
load = lambda n: json.load(open(os.path.join(src, n + ".geojson"), encoding="utf-8"))["features"]

countries = load("ne_10m_admin_0_countries")
places = load("ne_10m_populated_places")

KEEP = {"Sovereign country", "Country", "Sovereignty"}
# États classés « Disputed / Indeterminate » par Natural Earth mais qu'on garde dans la liste
EXTRA = {"ISR", "KOS", "PSX"}
# Capitales choisies quand Natural Earth en donne plusieurs ou aucune
OVERRIDES = {
    "ZAF": {"NAME": "Pretoria", "NAME_FR": "Pretoria", "LATITUDE": -25.7069, "LONGITUDE": 28.2294},
    "NRU": {"NAME": "Yaren", "NAME_FR": "Yaren", "LATITUDE": -0.5467, "LONGITUDE": 166.9211},
    "KAZ": {"NAME": "Astana", "NAME_FR": "Astana", "LATITUDE": 51.1694, "LONGITUDE": 71.4491},
    "TZA": {"NAME": "Dodoma", "NAME_FR": "Dodoma", "LATITUDE": -6.1630, "LONGITUDE": 35.7516},
    "BEN": {"NAME": "Porto-Novo", "NAME_FR": "Porto-Novo", "LATITUDE": 6.4969, "LONGITUDE": 2.6289},
}
NAME_OVERRIDES = {"CHN": "Chine"}
CAPITAL_NAME_OVERRIDES = {"UKR": "Kyiv"}
caps, by_name = {}, {}
for f in places:
    p = f["properties"]
    if p["FEATURECLA"] == "Admin-0 capital":
        caps.setdefault(p["ADM0_A3"], p)
        by_name.setdefault(p["ADM0NAME"], p)
for f in places:  # capitales régionales (Palestine : Ramallah…) en dernier recours
    p = f["properties"]
    if p["FEATURECLA"] == "Admin-0 region capital":
        by_name.setdefault(p["ADM0NAME"], p)
# Pays dont la capitale officielle n'est pas le siège du gouvernement (Bolivie, Pays-Bas…)
for f in places:
    p = f["properties"]
    if p["FEATURECLA"] == "Admin-0 capital alt":
        caps.setdefault(p["ADM0_A3"], p)

rows, missing = [], []
for f in countries:
    p = f["properties"]
    if p["TYPE"] not in KEEP and p["ADM0_A3"] not in EXTRA:
        continue
    c = (OVERRIDES.get(p["ADM0_A3"]) or caps.get(p["ADM0_A3"]) or by_name.get(p["NAME"]) or by_name.get(p["ADMIN"])
         or (caps.get(p["SOV_A3"]) if p["TYPE"] == "Sovereign country" else None))
    if c is None:
        missing.append(p["NAME_FR"] or p["NAME"]); continue
    rows.append({
        "name": NAME_OVERRIDES.get(p["ADM0_A3"]) or p["NAME_FR"] or p["NAME"],
        "capital": CAPITAL_NAME_OVERRIDES.get(p["ADM0_A3"]) or c["NAME_FR"] or c["NAME"],
        "lat": round(c["LATITUDE"], 5), "lon": round(c["LONGITUDE"], 5),
        "iso": p["ISO_A2"] if p["ISO_A2"] != "-99" else p["ADM0_A3"],
    })

import unicodedata
key = lambda s: unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode().lower()
rows.sort(key=lambda r: key(r["name"]))
# doublons éventuels (même pays sous deux TYPE)
seen, uniq = set(), []
for r in rows:
    if r["name"] in seen: continue
    seen.add(r["name"]); uniq.append(r)
json.dump(uniq, open(os.path.join(out, "countries.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=0)
print(len(uniq), "pays ; sans capitale :", missing)

lines = []
for f in load("ne_10m_admin_0_boundary_lines_land"):
    g = f["geometry"]
    parts = g["coordinates"] if g["type"] == "MultiLineString" else [g["coordinates"]]
    lines += [pts for pts in parts if len(pts) >= 2]
with gzip.open(os.path.join(out, "borders.bin.gz"), "wb", compresslevel=9) as fo:
    fo.write(b"SEBL" + struct.pack("<i", len(lines)))
    for pts in lines:
        fo.write(struct.pack("<i", len(pts)))
        fo.write(struct.pack(f"<{2*len(pts)}f", *[v for lon, lat in pts for v in (lat, lon)]))
print(len(lines), "lignes de frontière,", sum(map(len, lines)), "points")
