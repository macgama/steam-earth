"""Carte mondiale de couverture des sols (ESA WorldCover 2021) à basse résolution, incluse dans le jeu.

Sert pour les vues lointaines (espacement des sommets > 2 km), là où le jeu ne télécharge pas WorldCover.
Pour chaque case de 3° on lit l'aperçu le plus grossier du COG (≈ 640 m) et on garde la classe
majoritaire de chaque cellule de sortie.

Format : gzip( "SEL1" | int32 largeur | int32 hauteur | int32 0 | octets classe, ligne 0 = nord ) ;
classe 0 = pas de donnée (mer).

Usage : python build_landcover_base.py ../../data/landcover/world_cover.bin.gz [cellules_par_degré=20]
Licence des données : ESA WorldCover 2021, CC BY 4.0 (usage commercial autorisé, attribution requise).
"""
import gzip, json, struct, sys, urllib.request
from concurrent.futures import ThreadPoolExecutor
import numpy as np
import rasterio
from rasterio.enums import Resampling

ROOT = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021"
out_path = sys.argv[1]
per_deg = int(sys.argv[2]) if len(sys.argv) > 2 else 20
W, H = 360 * per_deg, 180 * per_deg
cell = 3 * per_deg

grid = json.load(urllib.request.urlopen(f"{ROOT}/esa_worldcover_grid.geojson"))
names = [f["properties"]["ll_tile"] for f in grid["features"]]
print(len(names), "cases WorldCover")
img = np.zeros((H, W), np.uint8)

def parse(n):
    lat = int(n[1:3]) * (1 if n[0] == "N" else -1)
    lon = int(n[4:7]) * (1 if n[3] == "E" else -1)
    return lat, lon

def work(n):
    url = f"/vsicurl/{ROOT}/map/ESA_WorldCover_10m_2021_v200_{n}_Map.tif"
    for attempt in range(3):
        try:
            with rasterio.Env(GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"):
                with rasterio.open(url) as d:
                    return n, d.read(1, out_shape=(cell, cell), resampling=Resampling.mode)
        except Exception as e:
            err = e
    print(n, "échec", err)
    return n, None

done = 0
with ThreadPoolExecutor(16) as ex:
    for n, a in ex.map(work, names):
        done += 1
        if a is None: continue
        lat, lon = parse(n)
        y0 = (90 - (lat + 3)) * per_deg
        x0 = (lon + 180) * per_deg
        img[y0:y0 + cell, x0:x0 + cell] = a
        if done % 100 == 0: print(done, "/", len(names), flush=True)

with gzip.open(out_path, "wb", 9) as f:
    f.write(b"SEL1" + struct.pack("<iii", W, H, 0) + img.tobytes())
print("écrit", out_path, W, H, {int(k): int(v) for k, v in zip(*np.unique(img, return_counts=True))})
