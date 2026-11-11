#!/usr/bin/env python3
"""Habitants de la Suisse, hectare par hectare, pour les passants (PNJ) du jeu.

Sources (données ouvertes de la Confédération, usage commercial autorisé avec indication de la source,
conditions « terms_by » d'opendata.swiss) :
- STATPOP 2025, Office fédéral de la statistique (OFS) : population résidante permanente par hectare,
  âge par tranches de 5 ans et sexe, nationalité, taille des ménages.
  https://data.geo.admin.ch/ch.bfs.statistik-bevoelkerung_haushalte/statistik-bevoelkerung_haushalte_2025/statistik-bevoelkerung_haushalte_2025_ha_2056.csv
- swissBOUNDARIES3D 2026, swisstopo : communes (nom, canton, district, habitants), pour dire où vit chacun.
  https://data.geo.admin.ch/ch.swisstopo.swissboundaries3d/swissboundaries3d_2026-01/swissboundaries3d_2026-01_2056_5728.shp.zip

Colonnes STATPOP utilisées : BBTOT (habitants), BBM01..BBM19 et BBW01..BBW19 (hommes / femmes de 0-4 ans,
5-9 ans… 90 ans et plus), BB11 (Suisses), BB13 (UE/AELE), BB14 (autres pays d'Europe), BB15 (hors Europe),
HP01..HP06 (ménages de 1, 2… 6 personnes et plus). Pour la protection des données, l'OFS remplace les valeurs
1 et 2 par 3 : les petits nombres sont un peu surestimés, les profils sont donc additionnés par carré de 1 km.

Sortie : data/population/statpop_ch.bin.gz (petit-boutiste)
  "POPC" | int32 version=1 | int32 année
  int32 nCommunes, puis par commune : uint16 n° OFS | uint8 canton | uint8 langue | uint8 2e langue |
      uint8 part de la 1re langue (0..255) | uint16 E, N du centre (hm − 24000 / − 10000, soit des centaines de m)
      | uint32 habitants | uint8 longueur | nom (UTF-8)
  int32 nBlocs (carrés de 1 km), puis par bloc : uint16 bx, by (km − 2480 / − 1070) |
      38 × uint8 âge-sexe (hommes 0-4…90+, femmes 0-4…90+) | 4 × uint8 nationalités (CH, UE/AELE, Europe, monde)
      | 6 × uint8 ménages (1…6+ personnes) ; chaque groupe est mis à l'échelle de son maximum (255)
  int32 nCellules (hectares habités, triés par N puis E), puis 4 tableaux de nCellules uint16 :
      E (hm − 24800), N (hm − 10700), habitants, n° de commune (index dans la table)
Langues : 0 allemand, 1 français, 2 italien, 3 romanche.

Usage :
  python3 build_population.py <statpop_ha.csv> <dossier_shp_swissboundaries3d> ../../data/population/statpop_ch.bin.gz
"""
import csv, gzip, os, struct, sys
from collections import defaultdict
import numpy as np
import shapefile

DE, FR, IT, RM = 0, 1, 2, 3
FR_CANTONS = {22, 24, 25, 26}  # VD, NE, GE, JU
DE_VS = {2301, 2304, 2306, 2309, 2313}  # Brig, Goms, Leuk, Raron, Visp (Haut-Valais)
IT_GR_DISTRICTS = {1842, 1847}  # Bernina, Moesa
RM_STRONG = {"Breil/Brigels", "Disentis/Mustér", "Lumnezia", "Medel (Lucmagn)", "Sumvitg", "Trun", "Tujetsch",
             "Waltensburg/Vuorz", "Surses", "Lantsch/Lenz", "Muntogna da Schons", "Scuol", "Zernez", "Valsot",
             "Val Müstair", "S-chanf", "Zuoz", "La Punt Chamues-ch", "Madulain"}
RM_MIXED = {"Ilanz/Glion", "Falera", "Schluein", "Sagogn", "Albula/Alvra", "Andeer", "Zillis-Reischen", "Samedan",
            "Bever", "Celerina/Schlarigna", "Pontresina", "Sils/Segl", "Silvaplana", "Domat/Ems", "Trin", "Bergün Filisur"}
BILINGUAL = {"Biel/Bienne": (DE, FR, 0.57), "Fribourg": (FR, DE, 0.75), "Murten": (DE, FR, 0.75),
             "Sierre": (FR, DE, 0.85), "Evolène": (FR, DE, 0.95), "Leuk": (DE, FR, 0.9)}


def language(name, canton, district):
    """Langue principale, deuxième langue, part de la principale."""
    if name in BILINGUAL:
        return BILINGUAL[name]
    if canton == 21:
        return IT, DE, 0.92
    if canton in FR_CANTONS:
        return FR, DE, 0.95
    if canton == 2:
        return (FR, DE, 0.85) if district == 241 else (DE, FR, 0.97)
    if canton == 10:
        return (DE, FR, 0.9) if district == 1006 else (DE, FR, 0.7) if district == 1005 else (FR, DE, 0.9)  # Singine, Lac
    if canton == 23:
        return (DE, FR, 0.95) if district in DE_VS else (FR, DE, 0.95)
    if canton == 18:
        if district in IT_GR_DISTRICTS or name == "Bregaglia":
            return IT, DE, 0.9
        if name in RM_STRONG or district == 1843 and name != "Samnaun":
            return RM, DE, 0.65
        if name in RM_MIXED:
            return DE, RM, 0.7
        return DE, RM, 0.97
    return DE, FR, 0.98


def read_communes(shp_dir):
    r = shapefile.Reader(os.path.join(shp_dir, "swissBOUNDARIES3D_1_5_TLM_HOHEITSGEBIET"))
    f = [x[0] for x in r.fields[1:]]
    communes = []
    for shp, rec in zip(r.shapes(), r.records()):
        d = dict(zip(f, rec))
        if d["ICC"] != "CH" or d["OBJEKTART"] != "Gemeindegebiet":
            continue
        pts = np.array(shp.points)[:, :2]
        parts = list(shp.parts) + [len(pts)]
        rings = [pts[a:b] for a, b in zip(parts[:-1], parts[1:])]
        communes.append(dict(bfs=d["BFS_NUMMER"], canton=d["KANTONSNUM"], district=d["BEZIRKSNUM"], name=d["NAME"],
                             pop=d["EINWOHNERZ"] or 0, rings=rings))
    # une commune peut avoir plusieurs morceaux (exclaves) : un seul enregistrement par n° OFS
    by = {}
    for c in communes:
        if c["bfs"] in by:
            by[c["bfs"]]["rings"] += c["rings"]
        else:
            by[c["bfs"]] = c
    return list(by.values())


def main(csv_path, shp_dir, out):
    rows = []
    with open(csv_path, newline="") as fh:
        rd = csv.reader(fh, delimiter=";")
        head = next(rd)
        ix = {h: i for i, h in enumerate(head)}
        age_cols = [ix[f"BBM{k:02d}"] for k in range(1, 20)] + [ix[f"BBW{k:02d}"] for k in range(1, 20)]
        nat_cols = [ix["BB11"], ix["BB13"], ix["BB14"], ix["BB15"]]
        hh_cols = [ix[f"HP{k:02d}"] for k in range(1, 7)]
        year = None
        for r in rd:
            if r[ix["NOLOC"]] != "0":
                continue
            year = int(r[ix["ERHJAHR"]])
            e, n, tot = int(r[ix["E_KOORD"]]) // 100, int(r[ix["N_KOORD"]]) // 100, int(r[ix["BBTOT"]])
            if tot <= 0:
                continue
            rows.append((e, n, tot, [int(r[i]) for i in age_cols], [int(r[i]) for i in nat_cols], [int(r[i]) for i in hh_cols]))
    print(f"{len(rows)} hectares habités, {sum(r[2] for r in rows):,} habitants (STATPOP {year})")

    # ---- blocs de 1 km
    blocks = defaultdict(lambda: [np.zeros(38), np.zeros(4), np.zeros(6)])
    for e, n, tot, age, nat, hh in rows:
        b = blocks[(e // 10, n // 10)]
        b[0] += age; b[1] += nat; b[2] += hh

    # ---- communes : chaque hectare habité prend la commune qui contient son centre
    communes = read_communes(shp_dir)
    print(f"{len(communes)} communes")
    cells = sorted(rows, key=lambda r: (r[1], r[0]))
    cell_e = np.array([c[0] for c in cells]); cell_n = np.array([c[1] for c in cells])
    commune_of = np.full(len(cells), -1, dtype=np.int64)
    row_index = defaultdict(list)
    for i, (e, n) in enumerate(zip(cell_e, cell_n)):
        row_index[int(n)].append(i)
    row_index = {k: np.array(v) for k, v in row_index.items()}
    for ci, c in enumerate(communes):
        edges = np.concatenate([np.stack([r[:-1, 0], r[:-1, 1], r[1:, 0], r[1:, 1]], axis=1) for r in c["rings"]])
        n0 = int(np.floor(edges[:, [1, 3]].min() / 100)); n1 = int(np.ceil(edges[:, [1, 3]].max() / 100))
        for n in range(n0, n1 + 1):
            if n not in row_index:
                continue
            y = n * 100 + 50
            m = (edges[:, 1] <= y) != (edges[:, 3] <= y)
            cand = edges[m]
            if len(cand) == 0:
                continue
            t = (y - cand[:, 1]) / (cand[:, 3] - cand[:, 1])
            xs = np.sort(cand[:, 0] + t * (cand[:, 2] - cand[:, 0]))
            idx = row_index[n]
            cx = cell_e[idx] * 100 + 50
            inside = (np.searchsorted(xs, cx) % 2) == 1
            commune_of[idx[inside]] = ci
    missing = np.where(commune_of < 0)[0]
    if len(missing):
        # hectares au bord d'un lac ou d'une frontière : commune de l'hectare habité le plus proche
        have = np.where(commune_of >= 0)[0]
        pts = np.stack([cell_e[have], cell_n[have]], axis=1)
        for i in missing:
            d = (pts[:, 0] - cell_e[i]) ** 2 + (pts[:, 1] - cell_n[i]) ** 2
            commune_of[i] = commune_of[have[np.argmin(d)]]
        print(f"{len(missing)} hectares rattachés à la commune voisine")

    # centre habité de chaque commune (moyenne pondérée par les habitants)
    acc = np.zeros((len(communes), 3))
    for i, c in enumerate(cells):
        acc[commune_of[i]] += (c[0] * c[2], c[1] * c[2], c[2])
    used = sorted(set(commune_of.tolist()))
    remap = {old: new for new, old in enumerate(used)}

    buf = bytearray(b"POPC" + struct.pack("<ii", 1, year))
    buf += struct.pack("<i", len(used))
    for old in used:
        c = communes[old]
        l1, l2, share = language(c["name"], c["canton"], c["district"])
        ce, cn = acc[old, 0] / acc[old, 2], acc[old, 1] / acc[old, 2]
        name = c["name"].encode("utf-8")[:255]
        buf += struct.pack("<HBBBBHHIB", c["bfs"], c["canton"], l1, l2, int(round(share * 255)),
                           int(round(ce)) - 24000, int(round(cn)) - 10000, int(c["pop"] or acc[old, 2]), len(name)) + name

    def q(v):
        m = v.max()
        return bytes(np.round(v / m * 255).astype(np.uint8)) if m > 0 else bytes(len(v))
    keys = sorted(blocks)
    buf += struct.pack("<i", len(keys))
    for (bx, by) in keys:
        age, nat, hh = blocks[(bx, by)]
        buf += struct.pack("<HH", bx - 2480, by - 1070) + q(age) + q(nat) + q(hh)

    buf += struct.pack("<i", len(cells))
    buf += (cell_e - 24800).astype("<u2").tobytes()
    buf += (cell_n - 10700).astype("<u2").tobytes()
    buf += np.minimum([c[2] for c in cells], 65535).astype("<u2").tobytes()
    buf += np.array([remap[int(x)] for x in commune_of]).astype("<u2").tobytes()

    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with gzip.open(out, "wb", compresslevel=9) as fh:
        fh.write(buf)
    print(f"{out} : {len(buf) / 1e6:.1f} Mo bruts, {os.path.getsize(out) / 1e6:.2f} Mo compressés, "
          f"{len(keys)} blocs de 1 km, {len(used)} communes")


if __name__ == "__main__":
    main(*sys.argv[1:4])
