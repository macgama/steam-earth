"""Météo mondiale réelle à partir de NOAA GFS (domaine public, usage commercial autorisé).

Télécharge uniquement les champs utiles (requêtes « Range » grâce aux fichiers .idx) de la dernière
prévision GFS 1° disponible sur AWS Open Data, et produit un fichier compact lu par le jeu
(WeatherProvider.cs).

Usage : python build_weather.py weather.bin.gz

Format (gzip) :
  'SEWX' + int32 longueur JSON + JSON (UTF-8) + données uint8 [échéance][canal][181 lat][360 lon]
  Grille : lat 90 → -90, lon 0 → 359 (pas de 1°). Décodage par canal décrit dans le JSON :
    linear : valeur = min + q/255·(max − min)
    sqrt   : valeur = max · (q/255)²
"""
import datetime as dt
import gzip, json, os, struct, sys, tempfile, urllib.request
import numpy as np
import eccodes

BUCKET = "https://noaa-gfs-bdp-pds.s3.amazonaws.com"
HOURS = list(range(3, 28, 3))  # échéances +3 h … +27 h

# nom, (paramètre GFS, niveau), encodage, min, max, unité
CHANNELS = [
    ("wind10_u", ("UGRD", "10 m above ground"), "linear", -50, 50, "m/s (vers l'est)"),
    ("wind10_v", ("VGRD", "10 m above ground"), "linear", -50, 50, "m/s (vers le nord)"),
    ("wind850_u", ("UGRD", "850 mb"), "linear", -80, 80, "m/s, ~1 500 m"),
    ("wind850_v", ("VGRD", "850 mb"), "linear", -80, 80, "m/s, ~1 500 m"),
    ("gust", ("GUST", "surface"), "linear", 0, 60, "m/s"),
    ("temp2m", ("TMP", "2 m above ground"), "linear", 213.15, 333.15, "K"),
    ("cloud_low", ("LCDC", "low cloud layer"), "linear", 0, 100, "%"),
    ("cloud_mid", ("MCDC", "middle cloud layer"), "linear", 0, 100, "%"),
    ("cloud_high", ("HCDC", "high cloud layer"), "linear", 0, 100, "%"),
    ("precip", ("PRATE", "surface"), "sqrt", 0, 50, "mm/h"),
    ("frozen", ("CPOFP", "surface"), "linear", 0, 100, "% de précipitations gelées"),
    ("visibility", ("VIS", "surface"), "sqrt", 0, 30000, "m"),
    # Neige au sol (modèle de surface de GFS : elle tombe, s'accumule et fond avec la météo réelle)
    ("snow_depth", ("SNOD", "surface"), "sqrt", 0, 4, "m"),
    # Altitude du relief du modèle (pour recaler neige et température sur le vrai relief)
    ("orography", ("HGT", "surface"), "linear", -500, 6500, "m"),
]

def get(url, rng=None):
    req = urllib.request.Request(url, headers={"Range": f"bytes={rng[0]}-{rng[1]}"} if rng else {})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()

def exists(url):
    try:
        urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=30); return True
    except Exception:
        return False

def latest_run():
    now = dt.datetime.now(dt.timezone.utc)
    for back in range(0, 48, 6):
        t = now - dt.timedelta(hours=back)
        run = t.replace(hour=t.hour // 6 * 6, minute=0, second=0, microsecond=0)
        base = f"{BUCKET}/gfs.{run:%Y%m%d}/{run:%H}/atmos/gfs.t{run:%H}z.pgrb2.1p00"
        if exists(f"{base}.f{HOURS[-1]:03d}.idx"):
            return run, base
    raise SystemExit("Aucune prévision GFS complète trouvée")

def ranges_for(idx_text):
    lines = [l.split(":") for l in idx_text.strip().splitlines()]
    out = {}
    for i, p in enumerate(lines):
        param, level, kind = p[3], p[4], p[5]
        if "ave" in kind or "acc" in kind:  # on veut les valeurs instantanées
            continue
        start = int(p[1])
        end = int(lines[i + 1][1]) - 1 if i + 1 < len(lines) else None
        out.setdefault((param, level), (start, end))
    return out

def decode(blob):
    with tempfile.NamedTemporaryFile(suffix=".grib2", delete=False) as f:
        f.write(blob); name = f.name
    with open(name, "rb") as f:
        h = eccodes.codes_grib_new_from_file(f)
        ni, nj = eccodes.codes_get(h, "Ni"), eccodes.codes_get(h, "Nj")
        vals = eccodes.codes_get_values(h).reshape(nj, ni)
        missing = eccodes.codes_get(h, "missingValue")
        eccodes.codes_release(h)
    os.unlink(name)
    vals = np.where(vals == missing, np.nan, vals)
    return vals

def quantize(v, enc, lo, hi):
    v = np.nan_to_num(v, nan=lo)
    if enc == "sqrt":
        q = np.sqrt(np.clip(v, 0, hi) / hi) * 255
    else:
        q = (np.clip(v, lo, hi) - lo) / (hi - lo) * 255
    return np.round(q).astype(np.uint8)

def main(out):
    run, base = latest_run()
    print("Prévision GFS", run.isoformat())
    frames, valid = [], []
    for fh in HOURS:
        idx = ranges_for(get(f"{base}.f{fh:03d}.idx").decode())
        planes = []
        for name, key, enc, lo, hi, _ in CHANNELS:
            if key not in idx:
                print(f"  f{fh:03d} {name} absent, mis à zéro"); planes.append(np.zeros((181, 360), np.uint8)); continue
            v = decode(get(f"{base}.f{fh:03d}", idx[key]))
            if name == "precip": v = v * 3600  # kg/m²/s → mm/h
            if name == "frozen": v = np.where(v < 0, 0, v)
            planes.append(quantize(v, enc, lo, hi))
        frames.append(np.stack(planes))
        valid.append((run + dt.timedelta(hours=fh)).strftime("%Y-%m-%dT%H:%M:%SZ"))
        print("  échéance", valid[-1])
    meta = {
        "source": "NOAA GFS 1° (domaine public)",
        "run": run.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "valid": valid,
        "width": 360, "height": 181, "lat0": 90, "lon0": 0, "step": 1,
        "channels": [{"name": n, "encoding": e, "min": lo, "max": hi, "unit": u} for n, _, e, lo, hi, u in CHANNELS],
    }
    js = json.dumps(meta, ensure_ascii=False).encode()
    with gzip.open(out, "wb", compresslevel=9) as f:
        f.write(b"SEWX" + struct.pack("<i", len(js)) + js)
        f.write(np.stack(frames).tobytes())
    print("écrit", out, os.path.getsize(out), "octets")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "weather.bin.gz")
