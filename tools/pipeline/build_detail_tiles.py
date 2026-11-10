"""Tuiles de détail 1°x1° à 30 m (Copernicus DEM GLO-30) + masque d'eau (ESA WorldCover).

Usage : python build_detail_tiles.py ../../data/detail LAT_MIN LAT_MAX LON_MIN LON_MAX
  ex. Suisse : python build_detail_tiles.py ../../data/detail 45 47 5 10
Chaque tuile NxxEyyy couvre [lat, lat+1] x [lon, lon+1], grille 3601x3601 (1"),
ligne 0 = nord. Les tuiles océaniques (absentes de Copernicus) sont ignorées.
"""
import os, sys
os.environ.setdefault("CURL_CA_BUNDLE", "/etc/ssl/certs/ca-certificates.crt")
import numpy as np
import rasterio
from rasterio.warp import reproject, Resampling
from rasterio.transform import from_origin
from common import write_height, write_water, stitch_edges

out = sys.argv[1]
la0, la1, lo0, lo1 = map(int, sys.argv[2:6])
N = 3601

def name(lat, lon):
    return f"{'N' if lat >= 0 else 'S'}{abs(lat):02d}{'E' if lon >= 0 else 'W'}{abs(lon):03d}"

def cop_url(lat, lon):
    t = f"Copernicus_DSM_COG_10_{'N' if lat >= 0 else 'S'}{abs(lat):02d}_00_{'E' if lon >= 0 else 'W'}{abs(lon):03d}_00_DEM"
    return f"/vsicurl/https://copernicus-dem-30m.s3.amazonaws.com/{t}/{t}.tif"

def wc_url(lat, lon):
    la, lo = (lat // 3) * 3, (lon // 3) * 3
    t = f"{'N' if la >= 0 else 'S'}{abs(la):02d}{'E' if lo >= 0 else 'W'}{abs(lo):03d}"
    return f"/vsicurl/https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{t}_Map.tif"

for lat in range(la0, la1):
    for lon in range(lo0, lo1):
        nm = name(lat, lon)
        dst = from_origin(lon - 0.5 / 3600, lat + 1 + 0.5 / 3600, 1 / 3600, 1 / 3600)
        h = np.zeros((N, N), np.float32)
        try:
            with rasterio.open(cop_url(lat, lon)) as s:
                reproject(rasterio.band(s, 1), h, dst_transform=dst, dst_crs="EPSG:4326", resampling=Resampling.bilinear)
        except Exception as e:
            print(nm, "pas de DEM (océan ?)", e); continue
        w = np.zeros((N, N), np.uint8)
        try:
            with rasterio.open(wc_url(lat, lon)) as s:
                win = rasterio.windows.from_bounds(lon - 0.01, lat - 0.01, lon + 1.01, lat + 1.01, s.transform)
                lc = s.read(1, window=win)
                reproject(lc, w, src_transform=s.window_transform(win), src_crs=s.crs,
                          dst_transform=dst, dst_crs="EPSG:4326", resampling=Resampling.mode)
            w = (w == 80).astype(np.uint8)
        except Exception as e:
            print(nm, "pas de WorldCover", e)
        write_height(os.path.join(out, nm + ".hgt.gz"), h)
        write_water(os.path.join(out, nm + ".wat.gz"), w)
        print(nm, "ok", h.min(), h.max(), w.mean())

stitch_edges(out)
print("bords raccordés")
