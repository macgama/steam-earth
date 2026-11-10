"""Fonds marins : découpe la grille mondiale GEBCO (15 secondes d'arc, ~450 m) en COG de 30° × 30°.

GEBCO Grid : domaine public, usage commercial autorisé, avec la mention
« GEBCO Compilation Group (année) GEBCO (année) Grid » (voir https://www.gebco.net/data-products/gridded-bathymetry-data).

Chaque fichier : int16, mètres par rapport au niveau de la mer ; les terres sont mises à 0 (seuls les fonds marins
servent au jeu, et le fichier se compresse bien mieux). Tuiles internes 512 × 512, DEFLATE + prédicteur,
aperçus ×2 … ×16 : le jeu (scripts/Data/CogReader.cs) n'en télécharge que les morceaux utiles.

Usage : python build_gebco_cogs.py <grille GEBCO : .vrt, .tif ou .nc> <dossier de sortie>
Les cases entièrement terrestres ne sont pas écrites (le jeu prend alors la carte mondiale de base).
"""
import json, os, sys
import numpy as np
from osgeo import gdal

gdal.UseExceptions()
src_path, out_dir = sys.argv[1], sys.argv[2]
os.makedirs(out_dir, exist_ok=True)
src = gdal.Open(src_path)
W, H = src.RasterXSize, src.RasterYSize
gt = src.GetGeoTransform()
ppd = round(1 / gt[1])  # pixels par degré (240 pour 15")
print("grille", W, "x", H, "origine", gt[0], gt[3], "pixels/degré", ppd)
assert abs(gt[0] + 180) < 1e-6 and abs(gt[3] - 90) < 1e-6, "grille mondiale attendue (-180, 90)"
band = src.GetRasterBand(1)
CELL = 30

def name(lat, lon):
    return f"gebco_{'N' if lat >= 0 else 'S'}{abs(lat):02d}{'E' if lon >= 0 else 'W'}{abs(lon):03d}.tif"

written = []
for lat in range(-90, 90, CELL):
    for lon in range(-180, 180, CELL):
        n = CELL * ppd
        x0, y0 = (lon + 180) * ppd, (90 - (lat + CELL)) * ppd
        a = band.ReadAsArray(x0, y0, n, n).astype(np.int16)
        a = np.minimum(a, 0)
        if not (a < 0).any():
            print(name(lat, lon), "entièrement terrestre, ignorée"); continue
        mem = gdal.GetDriverByName("MEM").Create("", n, n, 1, gdal.GDT_Int16)
        mem.SetGeoTransform((lon, gt[1], 0, lat + CELL, 0, gt[5]))
        mem.SetProjection(src.GetProjection() or 'GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]]')
        mem.GetRasterBand(1).WriteArray(a)
        out = os.path.join(out_dir, name(lat, lon))
        gdal.Translate(out, mem, format="COG", creationOptions=[
            "COMPRESS=DEFLATE", "PREDICTOR=YES", "LEVEL=9", "BLOCKSIZE=512",
            "OVERVIEWS=IGNORE_EXISTING", "OVERVIEW_COUNT=4", "RESAMPLING=AVERAGE", "BIGTIFF=NO", "NUM_THREADS=ALL_CPUS"])
        mem = None
        written.append(name(lat, lon))
        print(name(lat, lon), os.path.getsize(out) // 1024, "Ko, profondeur max", int(a.min()), "m")

json.dump({"cell_deg": CELL, "pixels_per_degree": ppd, "files": written}, open(os.path.join(out_dir, "gebco_info.json"), "w"), indent=1)
print(len(written), "fichiers écrits")
