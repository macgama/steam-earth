"""Utilitaires partagés : format de fichier des tuiles d'altitude.

Format .hgt.gz (lu par HeightTile.cs) :
  en-tête 16 octets little-endian : 'SEH1', int32 largeur, int32 hauteur, int32 réservé
  puis largeur*hauteur int16 (mètres), codés en delta par ligne, compressés gzip.
Format .wat.gz : même en-tête 'SEW1', puis largeur*hauteur octets (1 = eau), gzip.
"""
import gzip, struct
import numpy as np

def write_height(path, h):
    h = np.asarray(np.round(h), dtype=np.int16)
    d = h.copy()
    d[:, 1:] = (h[:, 1:].astype(np.int32) - h[:, :-1].astype(np.int32)).astype(np.int16)
    with gzip.open(path, "wb", compresslevel=9) as f:
        f.write(b"SEH1" + struct.pack("<iii", h.shape[1], h.shape[0], 0))
        f.write(d.astype("<i2").tobytes())

def write_water(path, m):
    m = np.asarray(m, dtype=np.uint8)
    with gzip.open(path, "wb", compresslevel=9) as f:
        f.write(b"SEW1" + struct.pack("<iii", m.shape[1], m.shape[0], 0))
        f.write(m.tobytes())

def read_height(path):
    with gzip.open(path, "rb") as f:
        buf = f.read()
    w, h = struct.unpack("<ii", buf[4:12])
    d = np.frombuffer(buf[16:], "<i2").reshape(h, w).astype(np.int32)
    return np.cumsum(d, axis=1).astype(np.int16)

def stitch_edges(folder):
    """Copernicus ne fournit pas la ligne sud ni la colonne est de chaque tuile (elles appartiennent
    aux voisines). On les recopie depuis la tuile voisine si elle existe, sinon depuis la ligne/colonne adjacente."""
    import os, re
    names = {f[:7] for f in os.listdir(folder) if f.endswith(".hgt.gz")}
    def parse(n):
        lat = int(n[1:3]) * (1 if n[0] == "N" else -1); lon = int(n[4:7]) * (1 if n[3] == "E" else -1)
        return lat, lon
    def name(lat, lon):
        return f"{'N' if lat >= 0 else 'S'}{abs(lat):02d}{'E' if lon >= 0 else 'W'}{abs(lon):03d}"
    tiles = {n: read_height(os.path.join(folder, n + ".hgt.gz")) for n in sorted(names)}
    for n, h in tiles.items():
        lat, lon = parse(n)
        h = h.copy()
        south, east, se = name(lat - 1, lon), name(lat, lon + 1), name(lat - 1, lon + 1)
        h[-1, :-1] = tiles[south][0, :-1] if south in tiles else h[-2, :-1]
        h[:-1, -1] = tiles[east][:-1, 0] if east in tiles else h[:-1, -2]
        h[-1, -1] = tiles[se][0, 0] if se in tiles else h[-2, -2]
        write_height(os.path.join(folder, n + ".hgt.gz"), h)
