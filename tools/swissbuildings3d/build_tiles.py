"""swissBUILDINGS3D 3.0 (swisstopo) → tuiles du jeu (PMTiles, zoom 14).

swisstopo publie swissBUILDINGS3D en libre accès (OGD, usage commercial autorisé, source à citer :
« © swisstopo ») : chaque bâtiment de Suisse et du Liechtenstein en 3D, avec ses vrais toits (pans, débords,
lucarnes, clochers), ses murs et son emprise au sol, en feuilles de ~4,4 × 3 km (1:25 000 / 16) au format
FileGDB. Le jeu ne sait pas lire ces fichiers (et une feuille de ville pèse ~45 Mo) : ce script les convertit
en tuiles compactes que le jeu télécharge à la demande (scripts/Data/SwissBuildings3D.cs).

Étapes :
  python3 build_tiles.py list  sheets.json                  # feuilles (version la plus récente de chacune)
  python3 build_tiles.py shard sheets.json K N out/part_K.bin # feuilles K, K+N, K+2N… (en parallèle)
  python3 build_tiles.py merge out/part_*.bin swissbuildings3d.pmtiles

Format d'une tuile (zoom 14, gzip) : "SB3D", u16 version, u32 nombre de bâtiments, puis pour chacun
(petit-boutiste) :
  u64 id (8 premiers octets de l'UUID swisstopo), i32 lat×1e7, i32 lon×1e7 (point d'ancrage),
  u8 drapeaux (1 = toit seul, sans murs : abri, couvert), 3 chaînes (u8 longueur + UTF-8) :
  OBJEKTART, NAME_KOMPLETT, GEBAEUDE_NUTZUNG, u8 unité (cm), f32 altitude du pied des murs (LN02),
  f32 bas et haut du toit (par rapport au pied), u8 nombre de contours au sol puis pour chacun u16 n et n × (i16 x, i16 y),
  u16 nombre de sommets puis n × (i16 x, i16 y, i16 z), u16 triangles de murs, u16 triangles de toit,
  puis les indices u16 (murs d'abord).
Les x, y sont en mètres (× unité) vers l'est et le nord dans le plan tangent au point d'ancrage, sur la sphère
de 6 371 km du jeu (même calcul que LocalFrame) ; les z sont comptés depuis le pied des murs.
Le premier contour au sol est le plus grand (sens direct), ses cours intérieures suivent (sens indirect).
"""
import gzip, json, math, os, struct, sys, tempfile, time, urllib.request, zipfile, shutil, collections

import numpy as np

API = "https://data.geo.admin.ch/api/stac/v0.9/collections/ch.swisstopo.swissbuildings3d_3_0/items?limit=100"
R = 6_371_000.0
ZOOM = 14
VERSION = 1


# ---------------------------------------------------------------- liste des feuilles

def get_json(url):
    for attempt in range(6):
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                return json.load(r)
        except Exception as e:
            print("  nouvel essai", attempt + 1, e, file=sys.stderr)
            time.sleep(2 ** attempt)
    raise SystemExit("API STAC injoignable")


def cmd_list(out):
    url, best = API, {}
    while url:
        d = get_json(url)
        for it in d["features"]:
            p = it["id"].split("_")  # swissbuildings3d_3_0_2021_1166-24 ; la couverture complète n'a pas de feuille
            if len(p) != 5:
                continue
            year, sheet = p[3], p[4]
            gdb = [a["href"] for k, a in it["assets"].items() if k.endswith(".gdb.zip")]
            if gdb and (sheet not in best or best[sheet][0] < year):
                best[sheet] = (year, gdb[0])
        url = next((l["href"] for l in d["links"] if l["rel"] == "next"), None)
    sheets = [{"sheet": s, "year": y, "url": u} for s, (y, u) in sorted(best.items())]
    with open(out, "w") as f:
        json.dump(sheets, f, indent=0)
    print(f"{len(sheets)} feuilles → {out}")


# ---------------------------------------------------------------- géométrie

def tin_triangles(g):
    """Triangles (3 × xyz) d'un TIN / d'une surface polyédrique OGR."""
    out = []
    for i in range(g.GetGeometryCount()):
        ring = g.GetGeometryRef(i).GetGeometryRef(0)
        pts = ring.GetPoints()
        if len(pts) >= 3:
            out.append(pts[:3])
    return out


def polygon_triangles(g):
    """Triangles d'un multipolygone 3D plan (chaque polygone est triangulé dans son propre plan)."""
    import mapbox_earcut as earcut
    out = []
    polys = [g.GetGeometryRef(i) for i in range(g.GetGeometryCount())] if g.GetGeometryName().startswith("MULTI") else [g]
    for p in polys:
        rings = [np.array(p.GetGeometryRef(k).GetPoints(), dtype=np.float64) for k in range(p.GetGeometryCount())]
        rings = [r[:-1] if len(r) > 1 and np.allclose(r[0], r[-1]) else r for r in rings]
        rings = [r for r in rings if len(r) >= 3]
        if not rings:
            continue
        pts = np.vstack(rings)
        o = rings[0]
        n = np.zeros(3)
        for i in range(len(o)):  # normale de Newell
            a, b = o[i], o[(i + 1) % len(o)]
            n += [(a[1] - b[1]) * (a[2] + b[2]), (a[2] - b[2]) * (a[0] + b[0]), (a[0] - b[0]) * (a[1] + b[1])]
        drop = int(np.argmax(np.abs(n)))
        keep = [k for k in range(3) if k != drop]
        ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)
        try:
            idx = earcut.triangulate_float64(pts[:, keep], ends)
        except Exception:
            continue
        for t in range(0, len(idx), 3):
            out.append([tuple(pts[idx[t + j]]) for j in range(3)])
    return out


def footprint(tris):
    """Emprise au sol (shapely) : réunion des triangles du sol."""
    import shapely
    from shapely.geometry import Polygon
    ps = [Polygon([(p[0], p[1]) for p in t]) for t in tris]
    ps = [p for p in ps if p.is_valid and p.area > 1e-5]
    if not ps:
        return None
    u = shapely.set_precision(shapely.union_all(shapely.set_precision(ps, 0.002)), 0.002).simplify(0.02)
    return u if not u.is_empty else None


def roof_outline(tris):
    import shapely
    from shapely.geometry import Polygon
    ps = [Polygon([(p[0], p[1]) for p in t]) for t in tris]
    ps = [p for p in ps if p.is_valid and p.area > 1e-5]
    return shapely.union_all(shapely.set_precision(ps, 0.01)) if ps else None


# ---------------------------------------------------------------- coordonnées

class Projector:
    def __init__(self):
        from osgeo import osr
        src = osr.SpatialReference(); src.ImportFromEPSG(2056)
        dst = osr.SpatialReference(); dst.ImportFromEPSG(4326)
        dst.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        src.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        self.t = osr.CoordinateTransformation(src, dst)

    def latlon(self, xy):
        r = np.array(self.t.TransformPoints([(float(x), float(y)) for x, y in xy]))
        return r[:, 1], r[:, 0]


def unit(lat, lon):
    la, lo = np.radians(lat), np.radians(lon)
    return np.stack([np.cos(la) * np.cos(lo), np.cos(la) * np.sin(lo), np.sin(la)], axis=-1)


def enu_basis(lat0, lon0):
    la, lo = math.radians(lat0), math.radians(lon0)
    c = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)])
    east = np.array([-math.sin(lo), math.cos(lo), 0.0])
    north = np.cross(c, east)
    return c, east, north


def tile_xy(lat, lon, z=ZOOM):
    n = 1 << z
    x = int((lon + 180) / 360 * n)
    lr = math.radians(lat)
    y = int((1 - math.log(math.tan(lr) + 1 / math.cos(lr)) / math.pi) / 2 * n)
    return x, y


# ---------------------------------------------------------------- une feuille

def s8(s):
    b = (s or "").strip().encode("utf-8")[:255]
    return struct.pack("<B", len(b)) + b


def process_gdb(path, proj):
    """Bâtiments d'une feuille : liste de (tuile x, tuile y, id, octets)."""
    from osgeo import ogr
    import shapely
    ogr.UseExceptions()
    ds = ogr.Open(path)
    attrs, walls, floors = {}, collections.defaultdict(list), collections.defaultdict(list)
    for f in ds.GetLayerByName("Wall"):
        u = f.GetField("UUID")
        attrs[u] = (f.GetField("OBJEKTART"), f.GetField("NAME_KOMPLETT"), f.GetField("GEBAEUDE_NUTZUNG"))
        g = f.GetGeometryRef()
        if g is not None:
            walls[u].extend(tin_triangles(g))
    for f in ds.GetLayerByName("Floor"):
        g = f.GetGeometryRef()
        if g is not None:
            floors[f.GetField("UUID")].extend(tin_triangles(g))
    blds = {}
    for u in set(walls) | set(floors):
        fp = footprint(floors.get(u) or [])
        if fp is None:
            # pas de sol : emprise = projection des murs (rare)
            ws = walls.get(u) or []
            if not ws:
                continue
            from shapely.geometry import MultiPoint
            fp = MultiPoint([(p[0], p[1]) for t in ws for p in t]).convex_hull
            if fp.area < 1:
                continue
        zs = [p[2] for t in (floors.get(u) or walls[u]) for p in t]
        blds[u] = {"fp": fp, "z0": min(zs), "walls": walls.get(u, []), "roofs": [], "attr": attrs.get(u, (None, None, None)),
                   "roofmin": math.inf, "roofmax": -math.inf}
    keys = list(blds)
    tree = shapely.STRtree([blds[k]["fp"] for k in keys]) if keys else None

    alone = []
    for f in ds.GetLayerByName("Roof"):
        g = f.GetGeometryRef()
        if g is None:
            continue
        tris = tin_triangles(g) if g.GetGeometryName() == "TIN" else polygon_triangles(g)
        if not tris:
            continue
        ol = roof_outline(tris)
        if ol is None or ol.area < 0.2:
            continue
        best, ba = None, 0.0
        if tree is not None:
            for i in tree.query(ol):
                a = blds[keys[i]]["fp"].intersection(ol).area
                if a > ba:
                    best, ba = keys[i], a
        zs = [p[2] for t in tris for p in t]
        # un toit peut couvrir plusieurs maisons (rangée) : il va à celle qu'il recouvre le plus
        if best is not None and ba >= 0.5:
            b = blds[best]
            b["roofs"].extend(tris)
            b["roofmin"] = min(b["roofmin"], min(zs)); b["roofmax"] = max(b["roofmax"], max(zs))
        else:
            # toit sans murs (abri, couvert, auvent de station) ; le pied est supposé 2,5 m sous le bord du toit
            alone.append({"fp": ol, "z0": min(zs) - 2.5, "walls": [], "roofs": tris, "roofmin": min(zs), "roofmax": max(zs),
                          "attr": (f.GetField("OBJEKTART"), f.GetField("NAME_KOMPLETT"), f.GetField("GEBAEUDE_NUTZUNG")),
                          "uuid": f.GetField("UUID"), "alone": True})
    out = []
    for u, b in blds.items():
        b["uuid"] = u
        b["alone"] = False
        if not b["roofs"]:
            zs = [p[2] for t in b["walls"] for p in t] or [b["z0"] + 3]
            b["roofmin"] = b["roofmax"] = max(zs)
        r = encode(b, proj)
        if r:
            out.append(r)
    for b in alone:
        r = encode(b, proj)
        if r:
            out.append(r)
    return out


def encode(b, proj):
    fp = b["fp"]
    polys = sorted(list(fp.geoms) if hasattr(fp, "geoms") else [fp], key=lambda p: -p.area)
    polys = [p for p in polys if p.geom_type == "Polygon" and p.area >= 1.0][:8]
    if not polys:
        return None
    walls = b["walls"]  # façades déjà en quadrilatères (2 triangles) : rien à regrouper
    roofs = b["roofs"]
    c = polys[0].representative_point() if not polys[0].centroid.within(polys[0]) else polys[0].centroid
    lat0, lon0 = (v[0] for v in proj.latlon([(c.x, c.y)]))
    # sommets (dédoublonnés au cm)
    vid, verts = {}, []

    def index(tris):
        out = []
        for t in tris:
            ids = []
            for p in t:
                k = (round(p[0] * 100), round(p[1] * 100), round(p[2] * 100))
                if k not in vid:
                    vid[k] = len(verts); verts.append(p)
                ids.append(vid[k])
            if len(set(ids)) == 3:
                out.append(ids)
        return out

    wall_idx, roof_idx = index(walls), index(roofs)
    n_wall, n_roof = len(wall_idx), len(roof_idx)
    tri_idx = wall_idx + roof_idx
    if len(verts) > 65000 or not tri_idx:
        return None
    rings = []
    for p in polys:
        ext = list(p.exterior.coords)[:-1]
        if p.exterior.is_ccw is False:
            ext.reverse()
        rings.append((True, ext))
        for h in p.interiors:
            hc = list(h.coords)[:-1]
            if h.is_ccw:
                hc.reverse()
            rings.append((False, hc))
    # projection : LV95 → lat/lon → plan tangent au point d'ancrage (sphère du jeu)
    allxy = [(p[0], p[1]) for p in verts] + [pt for _, r in rings for pt in r]
    la, lo = proj.latlon(allxy)
    cvec, east, north = enu_basis(lat0, lon0)
    d = unit(la, lo) - cvec
    ex, ny = d @ east * R, d @ north * R
    zs = np.array([p[2] for p in verts]) - b["z0"]
    ext = max(np.abs(ex).max(), np.abs(ny).max(), np.abs(zs).max())
    unit_cm = max(1, math.ceil(ext * 100 / 32000))
    if unit_cm > 255:
        return None
    q = 100.0 / unit_cm
    nv = len(verts)
    body = bytearray()
    body += struct.pack("<Q", int(b["uuid"].strip("{}").replace("-", "")[:16], 16))
    body += struct.pack("<ii", round(lat0 * 1e7), round(lon0 * 1e7))
    body += struct.pack("<B", 1 if b["alone"] else 0)
    for s in b["attr"]:
        body += s8(s)
    body += struct.pack("<Bfff", unit_cm, b["z0"], b["roofmin"] - b["z0"], b["roofmax"] - b["z0"])
    # contours : un contour extérieur et ses trous forment un polygone ; on stocke tous les anneaux à plat,
    # le signe de l'aire suffit au jeu pour les reconnaître
    body += struct.pack("<B", min(len(rings), 255))
    off = nv
    for _, r in rings[:255]:
        n = len(r)
        body += struct.pack("<H", n)
        xs = np.clip(np.round(ex[off:off + n] * q), -32767, 32767).astype("<i2")
        ys = np.clip(np.round(ny[off:off + n] * q), -32767, 32767).astype("<i2")
        body += np.stack([xs, ys], axis=1).tobytes()
        off += n
    body += struct.pack("<H", nv)
    v = np.stack([np.round(ex[:nv] * q), np.round(ny[:nv] * q), np.round(zs * q)], axis=1)
    body += np.clip(v, -32767, 32767).astype("<i2").tobytes()
    body += struct.pack("<HH", n_wall, n_roof)
    body += np.array(tri_idx, dtype="<u2").tobytes()
    tx, ty = tile_xy(lat0, lon0)
    return tx, ty, struct.unpack("<Q", bytes(body[:8]))[0], bytes(body)


# ---------------------------------------------------------------- shard / merge

def download(url, dest):
    for attempt in range(6):
        try:
            with urllib.request.urlopen(url, timeout=300) as r, open(dest, "wb") as f:
                shutil.copyfileobj(r, f, 1 << 20)
            return True
        except Exception as e:
            print("  nouvel essai", attempt + 1, url, e, file=sys.stderr)
            time.sleep(2 ** attempt)
    return False


def cmd_shard(sheets_json, k, n, out):
    sheets = json.load(open(sheets_json))[int(k)::int(n)]
    proj = Projector()
    t0 = time.time()
    count = 0
    with open(out + ".tmp", "wb") as fo:
        for i, s in enumerate(sheets):
            work = tempfile.mkdtemp(prefix="sb3d_")
            try:
                z = os.path.join(work, "s.zip")
                if not download(s["url"], z):
                    print("ÉCHEC", s["sheet"], file=sys.stderr)
                    continue
                with zipfile.ZipFile(z) as zf:
                    zf.extractall(work)
                gdb = next((os.path.join(work, d) for d in os.listdir(work) if d.endswith(".gdb")), None)
                if gdb is None:
                    continue
                recs = process_gdb(gdb, proj)
                for tx, ty, bid, data in recs:
                    fo.write(struct.pack("<HHQI", tx, ty, bid, len(data)))
                    fo.write(data)
                count += len(recs)
                print(f"[{k}] {i + 1}/{len(sheets)} {s['sheet']} ({s['year']}) : {len(recs)} bâtiments, "
                      f"{time.time() - t0:.0f} s", flush=True)
            except Exception as e:
                print("ERREUR", s["sheet"], repr(e), file=sys.stderr)
            finally:
                shutil.rmtree(work, ignore_errors=True)
    os.replace(out + ".tmp", out)
    print(f"[{k}] {count} bâtiments → {out}")


def cmd_merge(out, parts):
    from pmtiles.writer import Writer
    from pmtiles.tile import zxy_to_tileid, TileType, Compression
    tiles = collections.defaultdict(dict)
    for p in parts:
        with open(p, "rb") as f:
            while True:
                h = f.read(16)
                if len(h) < 16:
                    break
                tx, ty, bid, ln = struct.unpack("<HHQI", h)
                tiles[(tx, ty)].setdefault(bid, f.read(ln))  # bâtiment à cheval sur deux feuilles : une seule fois
    total = sum(len(v) for v in tiles.values())
    print(f"{total} bâtiments dans {len(tiles)} tuiles")
    order = sorted(tiles, key=lambda t: zxy_to_tileid(ZOOM, t[0], t[1]))
    lats, lons = [], []
    with open(out, "wb") as fo:
        w = Writer(fo)
        for tx, ty in order:
            recs = tiles[(tx, ty)]
            data = b"SB3D" + struct.pack("<HI", VERSION, len(recs)) + b"".join(recs[k] for k in sorted(recs))
            w.write_tile(zxy_to_tileid(ZOOM, tx, ty), gzip.compress(data, 9, mtime=0))
            n = 1 << ZOOM
            lons.append(tx / n * 360 - 180)
            lats.append(math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * ty / n)))))
        w.finalize(
            {
                "tile_type": TileType.UNKNOWN,
                "tile_compression": Compression.GZIP,
                "min_lon_e7": int(min(lons) * 1e7), "min_lat_e7": int((min(lats) - 0.02) * 1e7),
                "max_lon_e7": int((max(lons) + 0.03) * 1e7), "max_lat_e7": int(max(lats) * 1e7),
                "center_zoom": ZOOM, "center_lon_e7": int(7.44 * 1e7), "center_lat_e7": int(46.95 * 1e7),
            },
            {"name": "swissBUILDINGS3D 3.0", "attribution": "© swisstopo", "format": f"SB3D v{VERSION}",
             "buildings": total},
        )
    print(f"→ {out} ({os.path.getsize(out) / 1e6:.1f} Mo)")


if __name__ == "__main__":
    cmd, *a = sys.argv[1:]
    if cmd == "list":
        cmd_list(a[0])
    elif cmd == "shard":
        cmd_shard(*a)
    elif cmd == "merge":
        cmd_merge(a[-1], a[:-1])
    elif cmd == "gdb":  # test local : une feuille déjà décompressée
        recs = process_gdb(a[0], Projector())
        with open(a[1], "wb") as fo:
            for tx, ty, bid, data in recs:
                fo.write(struct.pack("<HHQI", tx, ty, bid, len(data))); fo.write(data)
        print(len(recs), "bâtiments", sum(len(r[3]) for r in recs) / 1e6, "Mo")
    else:
        raise SystemExit(__doc__)
