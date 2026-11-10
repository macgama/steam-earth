"""Index des tuiles swissALTI3D (1 km × 1 km, LV95) : pour chaque kilomètre, l'année de la version la plus récente.

swisstopo publie swissALTI3D en libre accès (OGD, usage commercial autorisé, source à citer :
« © swisstopo »). Le nom de chaque fichier contient l'année du relevé, différente selon la tuile :
le jeu a donc besoin de cet index pour construire l'URL sans interroger l'API STAC.

Sortie : data/swissalti3d_index.bin — en-tête "SAI1", E0, N0, largeur, hauteur (int32, km),
puis largeur×hauteur octets (année − 2000, 0 = pas de tuile), ligne 0 = sud.
Usage : python3 tools/swissalti3d/build_index.py
"""
import json, os, re, struct, sys, time, urllib.request

API = "https://data.geo.admin.ch/api/stac/v0.9/collections/ch.swisstopo.swissalti3d/items?limit=100"
E0, N0, W, H = 2480, 1070, 360, 230  # emprise de la Suisse en km LV95
PAT = re.compile(r"swissalti3d_(\d{4})_(\d{4})-(\d{4})_0\.5_2056_5728\.tif$")

def get(url):
    for attempt in range(6):
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                return json.load(r)
        except Exception as e:  # réseau instable : on réessaie
            print("  nouvel essai", attempt + 1, e, file=sys.stderr)
            time.sleep(2 ** attempt)
    raise SystemExit("API STAC injoignable")

def main():
    grid = bytearray(W * H)
    url, pages, items, odd = API, 0, 0, 0
    while url:
        d = get(url)
        for f in d["features"]:
            items += 1
            for name in f["assets"]:
                m = PAT.match(name)
                if not m:
                    continue
                year, e, n = int(m[1]), int(m[2]), int(m[3])
                x, y = e - E0, n - N0
                if not (0 <= x < W and 0 <= y < H):
                    odd += 1
                    continue
                grid[y * W + x] = max(grid[y * W + x], year - 2000)
        url = next((l["href"] for l in d["links"] if l["rel"] == "next"), None)
        pages += 1
        if pages % 50 == 0:
            print(f"{pages} pages, {items} tuiles", flush=True)
    out = os.path.join(os.path.dirname(__file__), "..", "..", "data", "swissalti3d_index.bin")
    with open(out, "wb") as fo:
        fo.write(b"SAI1" + struct.pack("<4i", E0, N0, W, H) + bytes(grid))
    print(f"{items} items, {sum(1 for v in grid if v)} km² indexés, {odd} hors emprise → {out}")

if __name__ == "__main__":
    main()
