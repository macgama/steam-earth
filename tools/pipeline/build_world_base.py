"""Carte d'altitude mondiale de base (basse résolution, avec bathymétrie).

Source : tuiles « Terrarium » (AWS Open Data, elevation-tiles-prod), qui agrègent
SRTM, GMTED, ETOPO1, etc. Reprojetée en équirectangulaire (lat/lon) 4096x2048 (~10 km/px).
Usage : python build_world_base.py ../../data/world_base.hgt.gz [zoom]
"""
import io, sys, math, urllib.request
import numpy as np
from PIL import Image
from concurrent.futures import ThreadPoolExecutor
from common import write_height

out = sys.argv[1]
z = int(sys.argv[2]) if len(sys.argv) > 2 else 4
n = 2 ** z
URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"

def fetch(xy):
    x, y = xy
    with urllib.request.urlopen(URL.format(z=z, x=x, y=y), timeout=60) as r:
        a = np.asarray(Image.open(io.BytesIO(r.read())).convert("RGB")).astype(np.float32)
    return xy, a[..., 0] * 256 + a[..., 1] + a[..., 2] / 256 - 32768

merc = np.zeros((n * 256, n * 256), np.float32)
with ThreadPoolExecutor(16) as ex:
    for (x, y), t in ex.map(fetch, [(x, y) for x in range(n) for y in range(n)]):
        merc[y * 256:(y + 1) * 256, x * 256:(x + 1) * 256] = t

W, H = 4096, 2048
lat = 90 - (np.arange(H) + 0.5) * 180 / H
lon = -180 + (np.arange(W) + 0.5) * 360 / W
latc = np.clip(lat, -85.05, 85.05)
my = (1 - np.log(np.tan(np.radians(latc)) + 1 / np.cos(np.radians(latc))) / math.pi) / 2 * merc.shape[0]
mx = (lon + 180) / 360 * merc.shape[1]
yi = np.clip(my.astype(int), 0, merc.shape[0] - 1)
xi = np.clip(mx.astype(int), 0, merc.shape[1] - 1)
eq = merc[yi[:, None], xi[None, :]]
# Au-delà de 85° (hors Mercator) : Antarctique ~2800 m au pôle Sud, océan au pôle Nord
eq[lat < -85.05, :] = 2800
eq[lat > 85.05, :] = -4000
eq = np.clip(eq, -11000, 8900)
write_height(out, eq)
print("ok", eq.min(), eq.max())
