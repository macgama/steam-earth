#!/usr/bin/env python3
"""Horaires officiels des transports publics suisses (GTFS) -> fichier compact pour le jeu (timetable_ch.bin.gz).

Source : opentransportdata.swiss, « Timetable 20xx (GTFS2020) » (tous les trains, trams, bus, bateaux,
funiculaires et téléphériques de Suisse, plus les trains transfrontaliers). Conditions d'utilisation :
usage libre, y compris commercial, avec mention de la source (https://opentransportdata.swiss/en/terms-of-use/).

Usage :
    python3 build_timetable.py gtfs.zip timetable_ch.bin.gz
    python3 build_timetable.py --download timetable_ch.bin.gz     # télécharge l'horaire de l'année en cours

Le GTFS (2,4 Go décompressé, 1,5 million de courses par an) est réduit en regroupant les courses qui
desservent les mêmes arrêts avec les mêmes temps de parcours (« motifs ») : une course = motif + heure
de départ + jours de circulation. Format (petit-boutiste, entiers « varint » LEB128, le tout en gzip) :

    "SETT" version(u8=1)
    feed_version(str) premier_jour(varint, AAAAMMJJ) nb_jours(varint)
    chaînes   : n, puis n × (longueur, UTF-8)           — l'indice 0 est la chaîne vide
    gares     : n, puis n × (nom, lat×1e6 zigzag, lon×1e6 zigzag)
    arrêts    : n, puis n × (gare, quai)                — un quai / poteau d'une gare
    exploitants : n, puis n × nom
    lignes    : n, puis n × (nom court, type GTFS, exploitant)
    calendriers : n, puis n × bits des jours (nb_jours bits, octets entiers)
    motifs    : n, puis n × (nb_arrêts, puis par arrêt : arrêt, arrivée (min depuis le départ),
                arrêt en gare (min), drapeaux : 1 = pas de montée, 2 = pas de descente)
    courses   : n, puis n × (motif, ligne, calendrier, départ (min depuis minuit, peut dépasser 24 h),
                destination, numéro)
Heures en heure légale suisse (Europe/Zurich).
"""
import csv, datetime as dt, gzip, io, os, re, struct, sys, urllib.request, zipfile

PERMALINK = "https://data.opentransportdata.swiss/dataset/timetable-{year}-gtfs2020/permalink"
# Copie de secours tenue par la Mobility Database (MobilityData), si le site officiel ne répond pas
MIRRORS = {2026: "https://storage.googleapis.com/download/storage/v1/b/mdb-latest/o/ch-unknown-swiss-federal-railways-sbb-gtfs-2898.zip?alt=media",
           2027: "https://storage.googleapis.com/download/storage/v1/b/mdb-latest/o/ch-unknown-swiss-federal-railways-sbb-gtfs-3619.zip?alt=media"}


def varint(n, out):
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return


def zigzag(n):
    return (n << 1) ^ (n >> 63)


def rows(z, name):
    f = io.TextIOWrapper(z.open(name), encoding="utf-8-sig", newline="")
    r = csv.reader(f)
    head = next(r)
    return {h: i for i, h in enumerate(head)}, r


def minutes(t):
    h, m, _ = t.split(":")
    return int(h) * 60 + int(m)


def download(dest):
    """Horaire de l'année en cours (il change mi-décembre : on prend l'année du lendemain + 3 semaines)."""
    year = (dt.date.today() + dt.timedelta(days=21)).year
    urls = [PERMALINK.format(year=year), MIRRORS.get(year), PERMALINK.format(year=year - 1), MIRRORS.get(year - 1)]
    for url in urls:
        if not url:
            continue
        try:
            print("téléchargement", url, flush=True)
            req = urllib.request.Request(url, headers={"User-Agent": "steam-earth-timetable"})
            with urllib.request.urlopen(req, timeout=600) as r, open(dest, "wb") as f:
                while True:
                    b = r.read(1 << 20)
                    if not b:
                        break
                    f.write(b)
            if zipfile.is_zipfile(dest):
                return dest
            print("  pas une archive GTFS", flush=True)
        except Exception as e:  # l'horaire de l'année suivante n'est publié qu'en automne
            print("  échec :", e, flush=True)
    raise SystemExit("aucun horaire GTFS téléchargeable")


def main():
    args = sys.argv[1:]
    if args and args[0] == "--download":
        src = download("gtfs_ch.zip")
        out_path = args[1] if len(args) > 1 else "timetable_ch.bin.gz"
    else:
        src, out_path = args[0], args[1]
    z = zipfile.ZipFile(src)

    strings, string_idx = [""], {"": 0}

    def s(v):
        v = v or ""
        i = string_idx.get(v)
        if i is None:
            i = string_idx[v] = len(strings)
            strings.append(v)
        return i

    # --- version et période
    h, r = rows(z, "feed_info.txt")
    fi = next(r)
    feed_version = fi[h["feed_version"]]
    start = dt.datetime.strptime(fi[h["feed_start_date"]], "%Y%m%d").date()
    end = dt.datetime.strptime(fi[h["feed_end_date"]], "%Y%m%d").date()
    ndays = (end - start).days + 1

    # --- gares et arrêts
    hs, r = rows(z, "stops.txt")  # en-tête gardé à part : les fonctions ci-dessous s'en servent plus tard
    raw_stops = {}
    for row in r:
        raw_stops[row[hs["stop_id"]]] = row
    stations, station_idx = [], {}

    def station_of(sid):
        row = raw_stops[sid]
        parent = row[hs["parent_station"]]
        key = parent if parent in raw_stops else sid
        i = station_idx.get(key)
        if i is None:
            pr = raw_stops[key]
            stations.append((s(pr[hs["stop_name"]]), float(pr[hs["stop_lat"]]), float(pr[hs["stop_lon"]])))
            i = station_idx[key] = len(stations) - 1
        return i

    stops, stop_idx = [], {}

    def stop(sid):
        i = stop_idx.get(sid)
        if i is None:
            row = raw_stops.get(sid)
            if row is None:
                raise KeyError(sid)
            stops.append((station_of(sid), s(row[hs["platform_code"]])))
            i = stop_idx[sid] = len(stops) - 1
        return i

    # --- exploitants et lignes
    h, r = rows(z, "agency.txt")
    agencies, agency_idx = [], {}
    for row in r:
        agency_idx[row[h["agency_id"]]] = len(agencies)
        agencies.append(s(row[h["agency_name"]]))
    h, r = rows(z, "routes.txt")
    routes, route_idx = [], {}
    for row in r:
        route_idx[row[h["route_id"]]] = len(routes)
        name = row[h["route_short_name"]] or row[h["route_long_name"]]
        routes.append((s(name), int(row[h["route_type"]]), agency_idx.get(row[h["agency_id"]], 0)))

    # --- calendriers : bits des jours, dédoublonnés
    h, r = rows(z, "calendar.txt")
    wd = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]
    cal = {}
    for row in r:
        a = dt.datetime.strptime(row[h["start_date"]], "%Y%m%d").date()
        b = dt.datetime.strptime(row[h["end_date"]], "%Y%m%d").date()
        days = [row[h[w]] == "1" for w in wd]
        bits = bytearray((ndays + 7) // 8)
        d = max(a, start)
        while d <= min(b, end):
            if days[d.weekday()]:
                k = (d - start).days
                bits[k >> 3] |= 1 << (k & 7)
            d += dt.timedelta(days=1)
        cal[row[h["service_id"]]] = bits
    h, r = rows(z, "calendar_dates.txt")
    for row in r:
        sid, date, typ = row
        bits = cal.setdefault(sid, bytearray((ndays + 7) // 8))
        k = (dt.date(int(date[:4]), int(date[4:6]), int(date[6:])) - start).days
        if 0 <= k < ndays:
            if typ == "1":
                bits[k >> 3] |= 1 << (k & 7)
            else:
                bits[k >> 3] &= ~(1 << (k & 7)) & 0xFF
    services, service_idx, service_of = [], {}, {}
    for sid, bits in cal.items():
        key = bytes(bits)
        i = service_idx.get(key)
        if i is None:
            i = service_idx[key] = len(services)
            services.append(key)
        service_of[sid] = i
    print(f"{len(cal)} calendriers -> {len(services)} distincts", flush=True)

    # --- courses
    h, r = rows(z, "trips.txt")
    trip_info = {}
    for row in r:
        trip_info[row[h["trip_id"]]] = (route_idx[row[h["route_id"]]], service_of[row[h["service_id"]]],
                                        s(row[h["trip_headsign"]]), s(row[h["trip_short_name"]]))
    print(f"{len(trip_info)} courses", flush=True)
    freq = {}
    h, r = rows(z, "frequencies.txt")
    for row in r:
        freq.setdefault(row[h["trip_id"]], []).append((minutes(row[h["start_time"]]), minutes(row[h["end_time"]]),
                                                       int(row[h["headway_secs"]]) // 60))

    patterns, pattern_idx, trips = [], {}, []
    skipped = 0

    def flush(tid, st):
        nonlocal skipped
        info = trip_info.get(tid)
        if info is None or len(st) < 2:
            skipped += 1
            return
        st.sort()
        t0 = st[0][2]  # départ du premier arrêt
        try:
            key = tuple((stop(sid), arr - t0, dep - arr, fl) for _, sid, dep, arr, fl in st)
        except KeyError:
            skipped += 1
            return
        p = pattern_idx.get(key)
        if p is None:
            p = pattern_idx[key] = len(patterns)
            patterns.append(key)
        if tid in freq:  # courses à intervalle régulier : une course par passage
            for a, b, step in freq[tid]:
                for t in range(a, b, max(step, 1)):
                    trips.append((p, info[0], info[1], t, info[2], info[3]))
        else:
            trips.append((p, info[0], info[1], t0, info[2], info[3]))

    h, r = rows(z, "stop_times.txt")
    i_tid, i_arr, i_dep, i_sid, i_seq = h["trip_id"], h["arrival_time"], h["departure_time"], h["stop_id"], h["stop_sequence"]
    i_pu, i_do = h.get("pickup_type"), h.get("drop_off_type")
    cur, st, n = None, [], 0
    for row in r:
        tid = row[i_tid]
        if tid != cur:
            if cur is not None:
                flush(cur, st)
            cur, st = tid, []
        arr, dep = row[i_arr], row[i_dep]
        arr = minutes(arr) if arr else minutes(dep)
        dep = minutes(dep) if dep else arr
        fl = (1 if i_pu is not None and row[i_pu] == "1" else 0) | (2 if i_do is not None and row[i_do] == "1" else 0)
        st.append((int(row[i_seq]), row[i_sid], dep, arr, fl))
        n += 1
        if n % 5_000_000 == 0:
            print(f"  {n:,} passages, {len(patterns):,} motifs", flush=True)
    if cur is not None:
        flush(cur, st)
    print(f"{n:,} passages -> {len(patterns):,} motifs, {len(trips):,} courses ({skipped} ignorées)", flush=True)

    trips.sort(key=lambda t: (t[1], t[0], t[3]))

    out = bytearray(b"SETT\x01")

    def wstr(v):
        b = v.encode("utf-8")
        varint(len(b), out)
        out.extend(b)

    wstr(feed_version)
    varint(int(start.strftime("%Y%m%d")), out)
    varint(ndays, out)
    varint(len(strings), out)
    for v in strings:
        wstr(v)
    varint(len(stations), out)
    for name, la, lo in stations:
        varint(name, out)
        varint(zigzag(round(la * 1e6)), out)
        varint(zigzag(round(lo * 1e6)), out)
    varint(len(stops), out)
    for sta, plat in stops:
        varint(sta, out)
        varint(plat, out)
    varint(len(agencies), out)
    for a in agencies:
        varint(a, out)
    varint(len(routes), out)
    for name, typ, ag in routes:
        varint(name, out)
        varint(typ, out)
        varint(ag, out)
    varint(len(services), out)
    for bits in services:
        out.extend(bits)
    varint(len(patterns), out)
    for key in patterns:
        varint(len(key), out)
        for sidx, arr, dwell, fl in key:
            varint(sidx, out)
            varint(max(arr, 0), out)
            varint(max(dwell, 0), out)
            varint(fl, out)
    varint(len(trips), out)
    for p, route, svc, t, head, num in trips:
        varint(p, out)
        varint(route, out)
        varint(svc, out)
        varint(t, out)
        varint(head, out)
        varint(num, out)
    with gzip.open(out_path, "wb", 9) as f:
        f.write(out)
    print(f"{len(stations):,} gares, {len(stops):,} arrêts, {len(routes):,} lignes : {len(out):,} octets -> "
          f"{os.path.getsize(out_path):,} octets gzip ({out_path})")


if __name__ == "__main__":
    main()
