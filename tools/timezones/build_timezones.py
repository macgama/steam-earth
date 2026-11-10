"""Carte mondiale des fuseaux horaires pour le jeu.

Source : timezone-boundary-builder (frontières des fuseaux IANA, dérivées d'OpenStreetMap,
licence ODbL), version « with-oceans » qui couvre aussi les océans (fuseaux Etc/GMT±N).
https://github.com/evansiroky/timezone-boundary-builder/releases

Usage :
  python build_timezones.py combined-with-oceans.json ../../data/timezones/timezones.bin.gz [pas en degrés]

Format (gzip) : 'SETZ', int32 largeur, int32 hauteur, int32 nombre de noms,
  puis chaque nom (uint8 longueur + UTF-8), puis largeur×hauteur uint16 (index du nom),
  ligne 0 = nord (90°), colonne 0 = -180°.
"""
import gzip, json, struct, sys
import numpy as np
from rasterio import features
from rasterio.transform import from_origin

src, out = sys.argv[1], sys.argv[2]
step = float(sys.argv[3]) if len(sys.argv) > 3 else 0.05
W, H = int(round(360 / step)), int(round(180 / step))

with open(src) as f:
    fc = json.load(f)
names = sorted({ft["properties"]["tzid"] for ft in fc["features"]})
index = {n: i for i, n in enumerate(names)}
shapes = ((ft["geometry"], index[ft["properties"]["tzid"]]) for ft in fc["features"])
grid = features.rasterize(shapes, out_shape=(H, W), transform=from_origin(-180, 90, step, step),
                          fill=65535, dtype="uint16", all_touched=False)

# Trous éventuels (pixels non couverts) : fuseau nautique selon la longitude
holes = grid == 65535
if holes.any():
    lon = -180 + (np.arange(W) + 0.5) * step
    for off in range(-12, 13):
        nm = "Etc/GMT" if off == 0 else f"Etc/GMT{'+' if off < 0 else '-'}{abs(off)}"  # signe inversé (POSIX)
        if nm not in index:
            index[nm] = len(names); names.append(nm)
    naut = np.round(lon / 15).astype(int)
    for x in np.unique(np.where(holes)[1]):
        off = int(naut[x])
        nm = "Etc/GMT" if off == 0 else f"Etc/GMT{'+' if off < 0 else '-'}{abs(off)}"
        col = grid[:, x]
        col[col == 65535] = index[nm]
print(f"{len(names)} fuseaux, grille {W}x{H}, trous comblés : {int(holes.sum())}")

with gzip.open(out, "wb", compresslevel=9) as f:
    f.write(b"SETZ" + struct.pack("<iii", W, H, len(names)))
    for n in names:
        b = n.encode()
        f.write(struct.pack("<B", len(b)) + b)
    f.write(grid.astype("<u2").tobytes())
