#!/usr/bin/env python3
"""Masque « Suisse + Liechtenstein » pour la règle « au sol seulement en Suisse ».

Source : swissBOUNDARIES3D (swisstopo, OGD, usage commercial OK avec « © swisstopo »), couche
TLM_LANDESGEBIET (territoire national : Suisse avec ses enclaves exclues — Büsingen, Campione — et Liechtenstein).

Sortie : data/geo/swiss_mask.bin.gz, grille lat/lon à 1/3600° (≈ 31 m nord-sud, 21 m est-ouest),
codée par plages : pour chaque ligne, les colonnes où l'on entre et sort du territoire.
  "SWMK" | int32 version=1 | float64 lat0, lon0 (coin sud-ouest) | int32 cellsPerDegree, rows, cols
  puis pour chaque ligne : uint16 n (pair) | n × uint16 colonnes [début, fin[ alternées.

Usage :
  curl -O https://data.geo.admin.ch/ch.swisstopo.swissboundaries3d/swissboundaries3d_2026-01/swissboundaries3d_2026-01_2056_5728.shp.zip
  unzip swissboundaries3d_2026-01_2056_5728.shp.zip
  pip install pyshp numpy
  python3 build_mask.py <dossier_shp> ../../data/geo/swiss_mask.bin.gz
"""
import gzip, os, struct, sys
import numpy as np
import shapefile

LAT0, LON0, LAT1, LON1 = 45.80, 5.94, 47.85, 10.52
CPD = 3600
KEEP = {"CH", "LI"}


def lv95_to_wgs84(e, n):
    """Formules approchées de swisstopo (précision ≈ 1 m)."""
    y = (e - 2_600_000) / 1e6
    x = (n - 1_200_000) / 1e6
    lam = 2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x - 0.0436 * y ** 3
    phi = 16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x - 0.0447 * y * y * x - 0.0140 * x ** 3
    return phi * 100 / 36, lam * 100 / 36


def main(src, out):
    path = os.path.join(src, "swissBOUNDARIES3D_1_5_TLM_LANDESGEBIET")
    r = shapefile.Reader(path)
    icc = [f[0] for f in r.fields[1:]].index("ICC")
    edges = []  # (lat_a, lon_a, lat_b, lon_b) de tous les anneaux (pair-impair : les trous s'annulent)
    for shp, rec in zip(r.shapes(), r.records()):
        if rec[icc] not in KEEP:
            continue
        pts = np.array(shp.points)
        lat, lon = lv95_to_wgs84(pts[:, 0], pts[:, 1])
        parts = list(shp.parts) + [len(pts)]
        for a, b in zip(parts[:-1], parts[1:]):
            la, lo = lat[a:b], lon[a:b]
            edges.append(np.stack([la[:-1], lo[:-1], la[1:], lo[1:]], axis=1))
        print(rec[icc], len(pts), "points", len(parts) - 1, "anneaux")
    e = np.concatenate(edges)
    rows = int(round((LAT1 - LAT0) * CPD))
    cols = int(round((LON1 - LON0) * CPD))
    assert cols < 65535
    print(f"grille {rows} × {cols}, {len(e)} segments")
    order = np.argsort(np.minimum(e[:, 0], e[:, 2]))
    e = e[order]
    lo_min = np.minimum(e[:, 0], e[:, 2])
    hi_max = np.maximum(e[:, 0], e[:, 2])
    buf = bytearray(b"SWMK" + struct.pack("<iddiii", 1, LAT0, LON0, CPD, rows, cols))
    inside = 0
    for row in range(rows):
        lat = LAT0 + (row + 0.5) / CPD
        k = np.searchsorted(lo_min, lat, side="right")
        cand = e[:k][hi_max[:k] > lat]
        cand = cand[(np.minimum(cand[:, 0], cand[:, 2]) <= lat)]
        # demi-ouvert pour ne pas compter deux fois un sommet
        m = (cand[:, 0] <= lat) != (cand[:, 2] <= lat)
        cand = cand[m]
        t = (lat - cand[:, 0]) / (cand[:, 2] - cand[:, 0])
        xs = np.sort(cand[:, 1] + t * (cand[:, 3] - cand[:, 1]))
        assert len(xs) % 2 == 0
        c = np.clip(np.round((xs - LON0) * CPD), 0, cols).astype(np.int64)
        runs = []
        for a, b in zip(c[0::2], c[1::2]):
            if b > a:
                if runs and runs[-1] >= a:  # plages qui se touchent
                    runs[-1] = max(runs[-1], b)
                else:
                    runs += [a, b]
        inside += sum(runs[1::2]) - sum(runs[0::2])
        buf += struct.pack("<H", len(runs)) + struct.pack(f"<{len(runs)}H", *runs)
    with gzip.open(out, "wb", 9) as f:
        f.write(buf)
    print(f"{inside} cases à l'intérieur, {len(buf)} octets → {os.path.getsize(out)} octets compressés")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
