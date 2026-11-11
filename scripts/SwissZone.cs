using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;

namespace SteamEarth;

/// <summary>
/// Règle de jeu : on ne joue au sol (à pied, vélo, voiture, bateau, atterrissage) qu'en Suisse et au Liechtenstein,
/// les seules régions à haute précision pour l'instant. Partout ailleurs, on survole la Terre sans toucher le sol
/// (vol libre ou avion), au moins <see cref="MinFlightHeight"/> au-dessus du relief.
///
/// La frontière est la vraie (swissBOUNDARIES3D © swisstopo, territoire national, enclaves de Büsingen et Campione
/// exclues), rastérisée à 1/3600° (≈ 31 × 21 m) par tools/swissmask/build_mask.py dans data/geo/swiss_mask.bin.gz.
/// Les lacs frontaliers sont coupés à la frontière comme sur la carte nationale.
/// </summary>
public static class SwissZone
{
    /// <summary>Hors de Suisse : hauteur minimale au-dessus du sol en vol libre et en avion (m).</summary>
    public const double MinFlightHeight = 300;
    /// <summary>Avion hors de Suisse : il n'est jamais plus bas que ça (au-dessus, il est remonté en douceur).</summary>
    public const double PlaneHardFloor = 120;

    public const string GroundOnlyInSwitzerland = "Hors de Suisse, on ne peut que survoler : seulement en Suisse à pied, à vélo, en voiture ou en bateau";
    public const string BorderBlocked = "Frontière suisse : au-delà, seulement en vol (G → avion)";
    public const string FlyOnly = "Hors de Suisse : survol seulement, au moins 300 m au-dessus du sol";
    public const string CannotLand = "Hors de Suisse, on ne peut pas se poser : seulement le survol";
    public const string CannotExit = "Impossible de descendre hors de Suisse";
    public const string OnlyPlane = "Hors de Suisse, seul l'avion est disponible";

    /// <summary>Affichage d'un message au joueur (branché sur les messages des véhicules par Main).</summary>
    public static Action<string> Notify;

    static double _lat0, _lon0;
    static int _cpd, _rows, _cols;
    static ushort[][] _runs;
    static bool _ready;

    /// <summary>Faux tant que le masque n'est pas chargé : alors tout est permis (aucune règle sans frontière).</summary>
    public static bool Ready => _ready;

    public static void Init()
    {
        try
        {
            var raw = Godot.FileAccess.GetFileAsBytes("res://data/geo/swiss_mask.bin.gz");
            using var z = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var br = new BinaryReader(z);
            if (new string(br.ReadChars(4)) != "SWMK" || br.ReadInt32() != 1) throw new InvalidDataException("format du masque");
            _lat0 = br.ReadDouble(); _lon0 = br.ReadDouble();
            _cpd = br.ReadInt32(); _rows = br.ReadInt32(); _cols = br.ReadInt32();
            _runs = new ushort[_rows][];
            for (int r = 0; r < _rows; r++)
            {
                int n = br.ReadUInt16();
                var a = new ushort[n];
                for (int i = 0; i < n; i++) a[i] = br.ReadUInt16();
                _runs[r] = a;
            }
            _ready = true;
            GD.Print($"[SwissZone] frontière chargée ({_rows} × {_cols} cases)");
        }
        catch (Exception e)
        {
            GD.PrintErr("[SwissZone] " + e.Message);
        }
    }

    /// <summary>Le point est-il en Suisse ou au Liechtenstein ?</summary>
    public static bool Contains(double lat, double lon)
    {
        if (!_ready) return true;
        int r = (int)Math.Floor((lat - _lat0) * _cpd), c = (int)Math.Floor((lon - _lon0) * _cpd);
        if (r < 0 || r >= _rows || c < 0 || c >= _cols) return false;
        var a = _runs[r];
        // plages [début, fin[ alternées : on compte les bornes ≤ c
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] <= c) lo = m + 1; else hi = m; }
        return (lo & 1) == 1;
    }

    public static bool Contains(Vec3d p)
    {
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        return Contains(lat, lon);
    }

    static readonly Dictionary<string, double> _said = new();

    /// <summary>Prévient le joueur (au plus une fois toutes les 4 s pour le même message).</summary>
    public static void Say(string message)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (_said.TryGetValue(message, out double t) && now - t < 4) return;
        _said[message] = now;
        if (Notify != null) Notify(message); else GD.Print("[SwissZone] " + message);
    }

    /// <summary>Avion hors de Suisse : il reste au-dessus de <see cref="MinFlightHeight"/> (remonté en douceur, jamais
    /// sous <see cref="PlaneHardFloor"/>). Hors de Suisse au sol (téléportation, chargement du relief) : remis en vol.</summary>
    public static void ConstrainPlane(Plane p, double dt)
    {
        if (Contains(p.PosD)) return;
        var up = p.PosD.Normalized();
        double ground = Vehicle.Ground(p.PosD, out _);
        double agl = p.PosD.Length() - Geo.Radius - ground;
        if (p.OnGround)
        {
            p.PlaceInAir(up * (Geo.Radius + ground + MinFlightHeight), p.Fwd, Math.Max(55, p.Vel.Length()));
            Say(CannotLand);
            return;
        }
        if (agl >= MinFlightHeight) return;
        double vn = p.Vel.Dot(up);
        double want = Math.Min(25, (MinFlightHeight - agl) * 0.3); // vitesse de montée visée (m/s)
        if (vn < want) p.Vel += up * ((want - vn) * Math.Min(1, dt * 2.5));
        if (agl < PlaneHardFloor) p.PosD = up * (Geo.Radius + ground + PlaneHardFloor);
        Say(FlyOnly);
    }
}
