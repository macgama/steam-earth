"""Lumières nocturnes des villes : NASA Black Marble 2016 (Suomi NPP VIIRS), domaine public.

Télécharge l'image mondiale en niveaux de gris (projection lat/lon) depuis NASA Earth Observatory,
la ramène à 8192×4096 (~5 km) et l'écrit au format du jeu :
  en-tête 16 octets : b"SENL", largeur (int32), hauteur (int32), 0 (int32)
  puis largeur×hauteur octets (0 = noir, 255 = très lumineux), ligne 0 = 90°N, colonne 0 = 180°O, le tout en gzip.

Usage : python build_nightlights.py sortie.bin.gz
Crédit à afficher : « NASA Earth Observatory / Black Marble (Suomi NPP VIIRS) ».
"""
import gzip
import io
import struct
import sys
import socket
import urllib.request

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

# L'image a changé d'hébergement en 2025 : on essaie les deux adresses, puis des variantes.
BASES = [
    "https://eoimages.gsfc.nasa.gov/images/imagerecords/144000/144898/",
    "https://assets.science.nasa.gov/content/dam/science/esd/eo/images/imagerecords/144000/144898/",
]
NAMES = [
    "BlackMarble_2016_3km_gray_geo.tif",
    "BlackMarble_2016_3km_gray.jpg",
    "BlackMarble_2016_3km_gray.tif",
    "BlackMarble_2016_3km_geo.tif",
    "BlackMarble_2016_3km.jpg",
    "BlackMarble_2016_01deg_gray_geo.tif",
    "BlackMarble_2016_01deg_gray.jpg",
    "BlackMarble_2016_01deg.jpg",
]
W, H = 8192, 4096

# eoimages.gsfc.nasa.gov n'est pas joignable en IPv6 depuis les runners GitHub : on force l'IPv4.
_gai = socket.getaddrinfo
socket.getaddrinfo = lambda host, *a, **k: [r for r in _gai(host, *a, **k) if r[0] == socket.AF_INET] or _gai(host, *a, **k)


def fetch():
    for name in NAMES:
        for base in BASES:
            url = base + name
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "steam-earth-pipeline"})
                with urllib.request.urlopen(req, timeout=300) as r:
                    data = r.read()
                print(f"téléchargé {url} ({len(data) / 1e6:.1f} Mo)")
                return data
            except Exception as e:  # noqa: BLE001
                print(f"échec {url} : {e}")
    sys.exit("aucune source Black Marble accessible")


def main():
    out = sys.argv[1]
    img = Image.open(io.BytesIO(fetch()))
    print("image source", img.size, img.mode)
    if img.mode == "L":
        g = np.asarray(img, dtype=np.float32)
    else:
        # Version couleur : les lumières sont jaune-blanc, les continents bleu sombre.
        # On garde ce qui est plus rouge que bleu, ce qui efface le fond bleuté.
        rgb = np.asarray(img.convert("RGB"), dtype=np.float32)
        g = np.clip((rgb[..., 0] - 0.8 * rgb[..., 2]) * 1.25, 0, 255)
    img = Image.fromarray(g.astype(np.uint8)).resize((W, H), Image.LANCZOS)
    a = np.asarray(img, dtype=np.uint8)
    print("min/moy/max", a.min(), round(float(a.mean()), 2), a.max())
    with gzip.open(out, "wb", compresslevel=9) as f:
        f.write(b"SENL" + struct.pack("<iii", W, H, 0))
        f.write(a.tobytes())


if __name__ == "__main__":
    main()
