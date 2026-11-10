#!/usr/bin/env python3
"""Aéroports du monde (OurAirports, domaine public) -> data/transport/airports.tsv.gz.

Source : https://github.com/davidmegginson/ourairports-data (airports.csv), mise à jour quotidienne,
données placées dans le domaine public (https://ourairports.com/data/) : utilisables dans un jeu vendu.
Garde les grands, moyens et petits aéroports et les hydrobases ouverts (pas les héliports ni les fermés).
Colonnes : lat, lon, type (L/M/S/W), vols réguliers (1/0), code IATA, code OACI, nom, ville.
Usage : python3 build_airports.py [airports.csv]
"""
import csv, gzip, io, os, sys, urllib.request

URL = "https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/airports.csv"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "data", "transport", "airports.tsv.gz")
TYPES = {"large_airport": "L", "medium_airport": "M", "small_airport": "S", "seaplane_base": "W"}

def clean(s):
    return (s or "").replace("\t", " ").replace("\n", " ").strip()

def main():
    raw = open(sys.argv[1], "rb").read() if len(sys.argv) > 1 else urllib.request.urlopen(URL).read()
    rows = []
    for r in csv.DictReader(io.StringIO(raw.decode("utf-8"))):
        t = TYPES.get(r["type"])
        if not t:
            continue
        try:
            lat, lon = float(r["latitude_deg"]), float(r["longitude_deg"])
        except ValueError:
            continue
        rows.append((t, f"{lat:.5f}", f"{lon:.5f}", t, "1" if r["scheduled_service"] == "yes" else "0",
                     clean(r["iata_code"]), clean(r["icao_code"] or r["gps_code"]), clean(r["name"]), clean(r["municipality"])))
    rows.sort(key=lambda x: "LMSW".index(x[0]))  # les plus grands d'abord
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with gzip.open(OUT, "wt", encoding="utf-8", compresslevel=9) as f:
        f.write("# OurAirports (domaine public) — lat\tlon\ttype\tregulier\tiata\toaci\tnom\tville\n")
        for r in rows:
            f.write("\t".join(r[1:]) + "\n")
    print(len(rows), "aéroports ->", os.path.getsize(OUT), "octets")

if __name__ == "__main__":
    main()
