using System;
using System.Collections.Generic;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Noms des rues, routes et places autour du joueur (OpenStreetMap via Overture Maps, couche « transportation »,
/// tuiles de zoom 14 — les mêmes que les routes, déjà en cache), affichés au-dessus de la chaussée quand on
/// s'en approche (≈ 250 m pour une ruelle, 600 m pour une route principale). Une étiquette par rue et par
/// tronçon de ~300 m, posée au milieu du plus long morceau. Touche N (avec les noms de lieux).
/// </summary>
public partial class StreetNamesLayer : LabelTileLayer
{
    protected override int Zoom => 14;
    protected override double LoadRadius(double agl) => agl < 800 ? 900 : 0;

    public override void _Ready() => Archive = Overture.OpenAsync("transportation");

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.N) ShowLayer = !ShowLayer;
    }

    static readonly HashSet<string> Layers = new() { "segment" };
    static readonly HashSet<string> Keys = new() { "subtype", "class", "@name", "names" };

    protected override List<Poi> Extract(byte[] mvt, int z, int x, int y)
    {
        var list = new List<Poi>();
        var layers = Mvt.Decode(mvt, Layers, Keys);
        if (!layers.TryGetValue("segment", out var feats)) return list;
        double tileM = 40_075_000.0 / (1 << z); // largeur de la tuile à l'équateur (m)
        PmTilesArchive.TileToLatLon(z, x, y, 0.5, 0.5, out double clat, out _);
        tileM *= Math.Cos(clat * Math.PI / 180);
        // plus long morceau par nom, regroupés ensuite par proximité
        var pieces = new List<(string name, string cls, double len, double fx, double fy)>();
        foreach (var f in feats)
        {
            if (f.Type != Mvt.GeomType.Line || f.Str("subtype") != "road") continue;
            string name = FrenchName(f.Str("names")) ?? f.Str("@name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            string cls = f.Str("class") ?? "";
            foreach (var pts in f.Parts)
            {
                double len = 0;
                for (int i = 2; i + 1 < pts.Length; i += 2) len += Math.Sqrt(Sq(pts[i] - pts[i - 2]) + Sq(pts[i + 1] - pts[i - 1]));
                if (len * tileM < 15) continue;
                // point au milieu de la longueur
                double half = len / 2, acc = 0, fx = pts[0], fy = pts[1];
                for (int i = 2; i + 1 < pts.Length; i += 2)
                {
                    double l = Math.Sqrt(Sq(pts[i] - pts[i - 2]) + Sq(pts[i + 1] - pts[i - 1]));
                    if (acc + l >= half && l > 0)
                    {
                        double t = (half - acc) / l;
                        fx = pts[i - 2] + (pts[i] - pts[i - 2]) * t;
                        fy = pts[i - 1] + (pts[i + 1] - pts[i - 1]) * t;
                        break;
                    }
                    acc += l;
                }
                if (fx < 0 || fx >= 1 || fy < 0 || fy >= 1) continue;
                pieces.Add((name.Trim(), cls, len * tileM, fx, fy));
            }
        }
        pieces.Sort((a, b) => b.len.CompareTo(a.len));
        var kept = new List<(string name, double fx, double fy)>();
        foreach (var p in pieces)
        {
            bool near = false;
            foreach (var k in kept)
                if (k.name == p.name && Math.Sqrt(Sq(k.fx - p.fx) + Sq(k.fy - p.fy)) * tileM < 300) { near = true; break; }
            if (near) continue;
            kept.Add((p.name, p.fx, p.fy));
            PmTilesArchive.TileToLatLon(z, x, y, p.fx, p.fy, out double lat, out double lon);
            bool major = p.cls is "motorway" or "trunk" or "primary" or "secondary";
            list.Add(new Poi
            {
                Key = "rue|" + p.name,
                Text = p.name,
                Lat = lat, Lon = lon,
                Lift = 3,
                Color = new Color(1f, 0.96f, 0.82f),
                FontSize = major ? 20 : 17,
                MaxDistance = major ? 600 : p.cls is "tertiary" ? 400 : 250,
                MergeDistance = 300,
            });
        }
        return list;
    }

    static double Sq(double v) => v * v;
}
