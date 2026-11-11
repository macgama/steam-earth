"""Index de toutes les villes, villes moyennes et villages de la Terre, pour la recherche du menu de départ.

Source : Overture Maps « divisions » (données OpenStreetMap, licence ODbL : usage commercial autorisé avec la mention
« © contributeurs OpenStreetMap », déjà affichée dans le jeu), localités de classe city, town, village (et celles sans
classe mais avec une population) : ~1,7 million de lieux.

Sortie (TSV compressé en Brotli si le nom finit par .br, sinon gzip ; une localité par ligne, villes d'abord puis par population décroissante) :
    nom affiché <TAB> autre nom (nom local s'il diffère, sinon vide) <TAB> code pays <TAB> région (code ISO 3166-2)
    <TAB> lat <TAB> lon <TAB> population <TAB> classe (c = ville, t = ville moyenne, v = village, l = autre localité)
Nom affiché : français, sinon anglais, sinon nom local ; un nom en écriture non latine sans nom français ni anglais
est translittéré (anyascii, licence ISC) pour pouvoir le chercher au clavier.

    pip install duckdb anyascii
    curl -o div.parquet https://overturemaps-us-west-2.s3.us-west-2.amazonaws.com/release/<VERSION>/theme=divisions/type=division/<FICHIER>.parquet
    python build_localities.py div.parquet ../../data/geo/localities.tsv.br
"""
import gzip, sys
import duckdb
from anyascii import anyascii

src, dst = sys.argv[1], sys.argv[2]
rows = duckdb.connect().sql(f"""
    select names."primary", coalesce(names.common['fr'], ''), coalesce(names.common['en'], ''),
           coalesce(country, ''), coalesce(region, ''),
           (bbox.ymin + bbox.ymax) / 2, (bbox.xmin + bbox.xmax) / 2, coalesce(population, 0), coalesce(class, '')
    from read_parquet('{src}')
    where subtype = 'locality' and names."primary" is not null
      and (class in ('city', 'town', 'village') or (class is null and population > 0))
""").fetchall()

rank = {"city": 0, "town": 1, "": 2, "village": 3}
rows.sort(key=lambda r: (rank[r[8]], -r[7], r[3], r[4], r[6]))  # à population égale : par pays et région (mieux compressé)
clean = lambda s: s.replace("\t", " ").replace("\n", " ").strip()
n = 0
import io
out = io.StringIO()
if True:
    for prim, fr, en, cc, region, lat, lon, pop, cls in rows:
        prim = clean(prim)
        name = clean(fr or en or prim)
        if not name:
            continue
        other = prim if prim != name else ""
        latin = anyascii(name)
        if latin != name and not any(c.isascii() and c.isalpha() for c in name):
            # écriture non latine sans nom français ni anglais : le nom translittéré devient le nom affiché
            other, name = name, latin.strip()
        reg = region.split("-", 1)[1] if "-" in region else ""
        if cls == "village" and other and not latin.isascii():
            pass  # nom local en écriture non latine : gardé (recherche possible par copier-coller)
        out.write(f"{name}\t{other}\t{cc}\t{reg}\t{lat:.4f}\t{lon:.4f}\t{pop or ''}\t{cls[0] if cls else 'l'}\n")
        n += 1
data = out.getvalue().encode("utf-8")
if dst.endswith(".br"):
    import brotli  # pip install brotli ; lu par System.IO.Compression.BrotliStream
    open(dst, "wb").write(brotli.compress(data, quality=11, lgwin=24))
else:
    open(dst, "wb").write(gzip.compress(data, 9))
print(n, "localités,", len(data) // 1_000_000, "Mo bruts")
