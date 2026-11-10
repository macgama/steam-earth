"""Vagues et houle réelles à partir de NOAA GFS-Wave (WAVEWATCH III, domaine public, usage commercial autorisé).

Même principe que tools/weather/build_weather.py : on ne télécharge sur AWS Open Data (noaa-gfs-bdp-pds) que les
champs utiles de la dernière prévision « gfswave global 0p25 », grâce aux fichiers .idx et aux requêtes « Range ».
La grille est réduite à 0,5° et écrite dans un fichier compact lu par le jeu (Waves.cs).

Usage : python build_waves.py waves.bin.gz [nombre d'échéances]

Format (gzip) : 'SEWX' + int32 longueur JSON + JSON (UTF-8) + uint8 [échéance][canal][361 lat][720 lon]
  Grille : lat 90 → -90, lon 0 → 359,5 (pas de 0,5°). 255 = pas de donnée (terre, banquise).
  Décodage (q = 0..254) :
    linear : valeur = min + q/254·(max − min)
    sqrt   : valeur = max · (q/254)²
    angle  : direction en degrés = q/254·360 (d'où viennent les vagues, 0 = nord, 90 = est)
"""
import datetime as dt
import gzip, json, os, struct, sys, tempfile, urllib.request
import numpy as np
import eccodes

BUCKET = "https://noaa-gfs-bdp-pds.s3.amazonaws.com"
HOURS = list(range(0, 28, 3))  # échéances +0 h … +27 h

# nom, (paramètre GRIB, niveau), encodage, min, max, unité
CHANNELS = [
    ("hs", ("HTSGW", "surface"), "sqrt", 0, 20, "m, hauteur significative totale"),
    ("wind_h", ("WVHGT", "surface"), "sqrt", 0, 20, "m, mer du vent"),
    ("wind_t", ("WVPER", "surface"), "linear", 0, 25, "s, période moyenne de la mer du vent"),
    ("wind_dir", ("WVDIR", "surface"), "angle", 0, 360, "degrés, provenance"),
    ("swell1_h", ("SWELL", "1 in sequence"), "sqrt", 0, 20, "m, houle principale"),
    ("swell1_t", ("SWPER", "1 in sequence"), "linear", 0, 25, "s"),
    ("swell1_dir", ("SWDIR", "1 in sequence"), "angle", 0, 360, "degrés, provenance"),
    ("swell2_h", ("SWELL", "2 in sequence"), "sqrt", 0, 20, "m, houle secondaire"),
    ("swell2_t", ("SWPER", "2 in sequence"), "linear", 0, 25, "s"),
    ("swell2_dir", ("SWDIR", "2 in sequence"), "angle", 0, 360, "degrés, provenance"),
]

def get(url, rng=None):
    hdr = {"Range": f"bytes={rng[0]}-{'' if rng[1] is None else rng[1]}"} if rng else {}
    with urllib.request.urlopen(urllib.request.Request(url, headers=hdr), timeout=120) as r:
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
        base = f"{BUCKET}/gfs.{run:%Y%m%d}/{run:%H}/wave/gridded/gfswave.t{run:%H}z.global.0p25"
        if exists(f"{base}.f{HOURS[-1]:03d}.grib2.idx"):
            return run, base
    raise SystemExit("Aucune prévision GFS-Wave complète trouvée")

def ranges_for(idx_text):
    lines = [l.split(":") for l in idx_text.strip().splitlines()]
    out = {}
    for i, p in enumerate(lines):
        start = int(p[1])
        end = int(lines[i + 1][1]) - 1 if i + 1 < len(lines) else None
        out.setdefault((p[3], p[4]), (start, end))
    return out

def decode(blob):
    with tempfile.NamedTemporaryFile(suffix=".grib2", delete=False) as f:
        f.write(blob); name = f.name
    with open(name, "rb") as f:
        h = eccodes.codes_grib_new_from_file(f)
        ni, nj = eccodes.codes_get(h, "Ni"), eccodes.codes_get(h, "Nj")
        lat0 = eccodes.codes_get(h, "latitudeOfFirstGridPointInDegrees")
        vals = eccodes.codes_get_values(h).reshape(nj, ni)
        bitmap = eccodes.codes_get(h, "bitmapPresent")
        missing = eccodes.codes_get(h, "missingValue")
        eccodes.codes_release(h)
    os.unlink(name)
    vals = np.where(vals == missing, np.nan, vals) if bitmap or True else vals
    if lat0 < 0: vals = vals[::-1]  # toujours nord → sud
    return vals

def quantize(v, enc, lo, hi):
    nan = np.isnan(v)
    v = np.nan_to_num(v, nan=lo)
    if enc == "sqrt":
        q = np.sqrt(np.clip(v, 0, hi) / hi) * 254
    elif enc == "angle":
        q = (np.mod(v, 360) / 360) * 254
    else:
        q = (np.clip(v, lo, hi) - lo) / (hi - lo) * 254
    q = np.round(q).astype(np.uint8)
    q[nan] = 255
    return q

def main(out, nframes=None):
    run, base = latest_run()
    print("Prévision GFS-Wave", run.isoformat())
    hours = HOURS[:nframes] if nframes else HOURS
    frames, valid = [], []
    for fh in hours:
        url = f"{base}.f{fh:03d}.grib2"
        idx = ranges_for(get(url + ".idx").decode())
        planes = []
        for name, key, enc, lo, hi, _ in CHANNELS:
            if key not in idx:
                print(f"  f{fh:03d} {name} absent"); planes.append(np.full((361, 720), 255, np.uint8)); continue
            v = decode(get(url, idx[key]))[::2, ::2]  # 0,25° → 0,5°
            planes.append(quantize(v, enc, lo, hi))
        frames.append(np.stack(planes))
        valid.append((run + dt.timedelta(hours=fh)).strftime("%Y-%m-%dT%H:%M:%SZ"))
        print("  échéance", valid[-1])
    meta = {
        "source": "NOAA GFS-Wave / WAVEWATCH III 0,25° (domaine public)",
        "run": run.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "valid": valid,
        "width": 720, "height": 361, "lat0": 90, "lon0": 0, "step": 0.5, "missing": 255,
        "channels": [{"name": n, "encoding": e, "min": lo, "max": hi, "unit": u} for n, _, e, lo, hi, u in CHANNELS],
    }
    js = json.dumps(meta, ensure_ascii=False).encode()
    with gzip.open(out, "wb", compresslevel=9) as f:
        f.write(b"SEWX" + struct.pack("<i", len(js)) + js)
        f.write(np.stack(frames).tobytes())
    print("écrit", out, os.path.getsize(out), "octets")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "waves.bin.gz", int(sys.argv[2]) if len(sys.argv) > 2 else None)
