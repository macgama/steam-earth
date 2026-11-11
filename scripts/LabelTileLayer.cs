using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Base commune des couches d'étiquettes posées au sol et chargées par tuiles vectorielles autour du joueur
/// (arrêts et gares : <see cref="TransitLayer"/> ; noms de rues : <see cref="StreetNamesLayer"/>).
/// Chaque étiquette = un nom (Label3D) et éventuellement un pictogramme (Sprite3D), qui s'estompent
/// à l'approche de leur distance maximale. Les doublons (même clé à moins de <see cref="Poi.MergeDistance"/>)
/// sont fusionnés, y compris d'une tuile à l'autre.
/// </summary>
public abstract partial class LabelTileLayer : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public bool ShowLayer = true;

    public sealed class Poi
    {
        public string Key;            // type + nom : sert à fusionner les doublons
        public string Text;
        public int Icon = -1;          // index du pictogramme, -1 = texte seul
        public Color Color = Colors.White;
        public int FontSize = 22;
        public double Lat, Lon;
        public double Lift = 8;        // hauteur au-dessus du sol (m)
        public double MaxDistance;
        public double MergeDistance = 200;
        public int Priority;           // à la fusion, la position de la plus haute priorité est gardée
        internal Vec3d Pos;
        internal bool Grounded;
        internal double SampledSpacing = double.MaxValue; // précision du relief déjà lue (m)
        internal int Refs;
        internal Node3D Node;
        internal Label3D Label;
        internal Sprite3D Sprite;
    }

    sealed class Tile { public int X, Y; public Vec3d Center; public double HalfSize; public bool Loading, Ready, Dead; public List<Poi> Pois = new(); }

    readonly Dictionary<(int, int), Tile> _tiles = new();
    readonly Dictionary<string, List<Poi>> _byKey = new();
    readonly List<Poi> _all = new();
    readonly ConcurrentQueue<(Tile, List<Poi>)> _done = new();
    protected Task<PmTilesArchive> Archive;
    double _clock, _nextScan;
    int _loading;
    protected Texture2D Icons;
    protected int IconSize = 64;
    const float LabelPixel = 0.0008f, IconPixel = 0.00042f;

    public int Count => _all.Count;
    public bool Idle => _loading == 0 && _done.IsEmpty;

    /// <summary>Zoom des tuiles lues.</summary>
    protected abstract int Zoom { get; }
    /// <summary>Rayon de chargement (m) selon la hauteur au-dessus du sol ; ≤ 0 = rien à charger.</summary>
    protected abstract double LoadRadius(double agl);
    /// <summary>Extrait les étiquettes d'une tuile (appelé hors du fil principal).</summary>
    protected abstract List<Poi> Extract(byte[] mvt, int z, int x, int y);

    public override void _Process(double delta)
    {
        _clock += delta;
        while (_done.TryDequeue(out var item)) Install(item.Item1, item.Item2);
        if (_clock >= _nextScan && Archive != null && Archive.IsCompletedSuccessfully && Archive.Result != null)
        {
            _nextScan = _clock + 0.5;
            Scan();
        }
        UpdateLabels();
    }

    protected void UpdateLabels()
    {
        var cam = Camera.PosD;
        foreach (var p in _all)
        {
            double d = (p.Pos - cam).Length();
            bool vis = ShowLayer && d < p.MaxDistance;
            p.Node.Visible = vis;
            if (!vis) continue;
            // Précision du relief selon la distance : un aéroport visible à 300 km n'a pas besoin du 30 m
            // (le lire à chaque image chargeait des blocs entiers de données détaillées tout autour du joueur).
            double want = d < 3000 ? 2 : Math.Max(30, d / 40);
            if (!p.Grounded && want < p.SampledSpacing)
            {
                float h = HeightProvider.Sample(p.Lat, p.Lon, want, out bool water, out bool pending);
                if (!pending) p.SampledSpacing = want * 0.7; // affinée seulement quand on s'est bien rapproché
                p.Grounded = !pending && d < 3000;
                p.Pos = Geo.FromLatLon(p.Lat, p.Lon) * (Geo.Radius + (water ? Math.Max(h, 0) : h) + p.Lift);
            }
            p.Node.Position = (p.Pos - cam).ToVector3();
            float fade = (float)Math.Clamp((p.MaxDistance - d) / (p.MaxDistance * 0.25), 0, 1);
            p.Label.Modulate = new Color(p.Color, fade);
            p.Label.OutlineModulate = new Color(0.05f, 0.05f, 0.08f, 0.8f * fade);
            if (p.Sprite != null) p.Sprite.Modulate = new Color(1, 1, 1, fade);
        }
    }

    void Scan()
    {
        var dir = Camera.PlayerPos.Normalized();
        Geo.ToLatLon(dir, out double lat, out double lon);
        double agl = Math.Max(0, Camera.Altitude - Camera.GroundHeight);
        double radius = LoadRadius(agl) * GraphicsQuality.Current.LabelsRadiusScale;
        var wanted = new HashSet<(int, int)>();
        int z = Zoom, n = 1 << z;
        if (radius > 0)
        {
            double dLat = radius / 111_320, dLon = Math.Min(radius / (111_320 * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05)), 179);
            PmTilesArchive.TileXY(Math.Min(lat + dLat, 85), lon - dLon, z, out int x0, out int y0);
            PmTilesArchive.TileXY(Math.Max(lat - dLat, -85), lon + dLon, z, out int x1, out int y1);
            if (lon - dLon < -180) x0 -= n; // traverse l'antiméridien
            if (lon + dLon > 180) x1 += n;
            for (int y = y0; y <= y1; y++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int x = ((xx % n) + n) % n;
                    var key = (x, y);
                    if (!_tiles.TryGetValue(key, out var t))
                    {
                        PmTilesArchive.TileToLatLon(z, x, y, 0.5, 0.5, out double cla, out double clo);
                        PmTilesArchive.TileToLatLon(z, x, y, 0, 0, out double ala, out double alo);
                        var c = Geo.FromLatLon(cla, clo);
                        t = new Tile { X = x, Y = y, Center = c, HalfSize = (Geo.FromLatLon(ala, alo) - c).Length() * Geo.Radius };
                    }
                    if ((t.Center - dir).Length() * Geo.Radius - t.HalfSize > radius) continue;
                    wanted.Add(key);
                    _tiles.TryAdd(key, t);
                }
        }
        var remove = new List<(int, int)>();
        foreach (var (key, t) in _tiles)
            if (!wanted.Contains(key) && !t.Loading) remove.Add(key);
        foreach (var key in remove)
        {
            var t = _tiles[key];
            t.Dead = true;
            foreach (var p in t.Pois) Release(p);
            _tiles.Remove(key);
        }
        // les plus proches d'abord
        var todo = new List<Tile>();
        foreach (var key in wanted)
        {
            var t = _tiles[key];
            if (!t.Ready && !t.Loading) todo.Add(t);
        }
        todo.Sort((a, b) => (a.Center - dir).Length().CompareTo((b.Center - dir).Length()));
        foreach (var t in todo)
        {
            if (_loading >= 3) break;
            Load(t);
        }
    }

    void Load(Tile t)
    {
        t.Loading = true;
        _loading++;
        var archive = Archive.Result;
        int z = Zoom;
        Task.Run(async () =>
        {
            List<Poi> list = null;
            try
            {
                var bytes = await archive.GetTileAsync(z, t.X, t.Y);
                list = bytes == null ? new List<Poi>() : Extract(bytes, z, t.X, t.Y);
            }
            catch (Exception e) { GD.PrintErr($"[{GetType().Name}] tuile {z}/{t.X}/{t.Y} : {e.Message}"); }
            _done.Enqueue((t, list));
        });
    }

    void Install(Tile t, List<Poi> list)
    {
        _loading--;
        t.Loading = false;
        if (t.Dead || list == null) return; // list null : réessai plus tard
        t.Ready = true;
        foreach (var p in list) t.Pois.Add(Add(p));
    }

    /// <summary>Ajoute une étiquette, ou renvoie le doublon déjà affiché (même clé, tout près).</summary>
    protected Poi Add(Poi p)
    {
        p.Pos = Geo.FromLatLon(p.Lat, p.Lon) * (Geo.Radius + HeightProvider.SurfaceHeight(p.Lat, p.Lon, 3000) + p.Lift);
        if (_byKey.TryGetValue(p.Key, out var same))
            foreach (var q in same)
                if ((q.Pos - p.Pos).Length() < Math.Max(p.MergeDistance, q.MergeDistance))
                {
                    q.Refs++;
                    return q;
                }
        p.Refs = 1;
        p.Label = new Label3D
        {
            Text = p.Text,
            FixedSize = true,
            PixelSize = LabelPixel,
            FontSize = p.FontSize,
            OutlineSize = 7,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        if (p.Icon >= 0 && Icons != null)
        {
            p.Sprite = new Sprite3D
            {
                Texture = Icons,
                RegionEnabled = true,
                RegionRect = new Rect2(0, p.Icon * IconSize, IconSize, IconSize),
                FixedSize = true,
                PixelSize = IconPixel,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            };
            // le nom à droite du pictogramme
            p.Label.HorizontalAlignment = HorizontalAlignment.Left;
            p.Label.Offset = new Vector2(IconSize * 0.5f * IconPixel / LabelPixel + 6, 0);
            p.Sprite.AddChild(p.Label);
            p.Node = p.Sprite;
        }
        else p.Node = p.Label;
        p.Node.Visible = false;
        Planet.AddChild(p.Node);
        if (!_byKey.TryGetValue(p.Key, out same)) _byKey[p.Key] = same = new List<Poi>();
        same.Add(p);
        _all.Add(p);
        return p;
    }

    protected void Release(Poi p)
    {
        if (--p.Refs > 0) return;
        p.Node.QueueFree();
        _all.Remove(p);
        if (_byKey.TryGetValue(p.Key, out var same)) { same.Remove(p); if (same.Count == 0) _byKey.Remove(p.Key); }
    }

    /// <summary>Pictogrammes : data/transport/icons.rgba.gz (voir tools/transport/build_icons.py).</summary>
    protected static Texture2D LoadIcons(string path, out int size)
    {
        size = 64;
        try
        {
            var gz = Godot.FileAccess.GetFileAsBytes(path);
            using var ms = new System.IO.MemoryStream();
            using (var z = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(gz), System.IO.Compression.CompressionMode.Decompress))
                z.CopyTo(ms);
            var all = ms.ToArray();
            int nl = Array.IndexOf(all, (byte)'\n');
            var head = System.Text.Encoding.ASCII.GetString(all, 0, nl).Split(' ');
            size = int.Parse(head[0]);
            int count = int.Parse(head[1]);
            var px = new byte[size * size * 4 * count];
            Array.Copy(all, nl + 1, px, 0, px.Length);
            // icônes empilées verticalement : une image size × (size·count)
            var img = Image.CreateFromData(size, size * count, false, Image.Format.Rgba8, px);
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[LabelTileLayer] pictogrammes illisibles ({path}) : {e.Message}");
            return null;
        }
    }

    /// <summary>Nom français (names.common.fr) s'il existe.</summary>
    protected static string FrenchName(string json)
    {
        if (json == null || !json.Contains("\"fr\"")) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("common", out var c) && c.TryGetProperty("fr", out var fr)) return fr.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    /// <summary>Point représentatif d'une entité (point, milieu d'une ligne, moyenne d'un contour), en fraction de tuile.</summary>
    protected static bool Anchor(Mvt.Feature f, out double fx, out double fy)
    {
        fx = fy = 0;
        if (f.Parts.Count == 0 || f.Parts[0].Length < 2) return false;
        var pts = f.Parts[0];
        if (f.Type == Mvt.GeomType.Point) { fx = pts[0]; fy = pts[1]; }
        else if (f.Type == Mvt.GeomType.Line) { int m = pts.Length / 4 * 2; fx = pts[m]; fy = pts[m + 1]; }
        else
        {
            int k = pts.Length / 2;
            for (int i = 0; i < k; i++) { fx += pts[2 * i]; fy += pts[2 * i + 1]; }
            fx /= k; fy /= k;
        }
        return fx >= 0 && fx < 1 && fy >= 0 && fy < 1; // dans la marge : c'est la tuile voisine qui l'affiche
    }
}
