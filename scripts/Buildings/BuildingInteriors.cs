using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth;

/// <summary>
/// Intérieurs des bâtiments, générés autour du joueur (rayon et nombre selon le niveau de qualité graphique) :
/// quand un intérieur est prêt, le volume plein du bâtiment ne compte plus pour les collisions et sa porte
/// s'ouvre (le shader des façades l'efface à moins de <see cref="BuildingsLayer.DoorOpenDistance"/> m du joueur).
/// L'étage où se trouve le joueur est meublé (et ceux autour) ; en changeant d'étage, l'intérieur est refait.
/// Les commerces ont leur enseigne au-dessus de la porte. PgUp / PgDn : ascenseur (immeubles de 5 niveaux ou plus).
/// </summary>
public partial class BuildingInteriors : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public BuildingsLayer Buildings;
    public float Night;

    /// <summary>Le joueur est dans un bâtiment dont l'intérieur est construit (la caméra se rapproche).</summary>
    public static bool PlayerInside { get; private set; }

    sealed class Interior
    {
        public BuildingInfo Info;
        public MeshInstance3D Node;
        public InteriorData Data;
        public bool Building, Dead;
        public int WantedFocus;
    }

    readonly Dictionary<string, Interior> _active = new();
    readonly ConcurrentQueue<(Interior it, InteriorData data)> _done = new();
    readonly Dictionary<string, Label3D> _signs = new();
    readonly List<(BuildingInfo info, double dist)> _near = new();
    ShaderMaterial _mat;
    double _clock, _nextScan;
    int _building;
    Interior _inside;
    int _insideFloor = -1;
    int _infosVersion = -1;
    Label _hint;

    public int Count => _active.Count;
    public bool Idle => _building == 0 && _done.IsEmpty;
    public string Status => _inside != null && _inside.Data != null
        ? $"dans {Describe(_inside.Info)}, {FloorName(_insideFloor)} ({_inside.Data.AccessibleFloors} niveau{(_inside.Data.AccessibleFloors > 1 ? "x" : "")})"
        : $"{_active.Count} intérieur{(_active.Count > 1 ? "s" : "")} prêt{(_active.Count > 1 ? "s" : "")}";

    static string Describe(BuildingInfo b) => b.Name != null ? $"{BuildingKinds.Label(b.Kind)} « {b.Name} »" : BuildingKinds.Label(b.Kind);
    static string FloorName(int k) => k <= 0 ? "rez-de-chaussée" : k == 1 ? "1er étage" : $"{k}e étage";

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        var layer = new CanvasLayer { Layer = 1 };
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AddThemeColorOverride("font_color", new Color(1, 0.92f, 0.7f));
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        _hint.AddThemeConstantOverride("outline_size", 6);
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hint.Position = new Vector2(-400, -70);
        _hint.Size = new Vector2(800, 30);
        layer.AddChild(_hint);
        AddChild(layer);
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        while (_done.TryDequeue(out var r)) Install(r.it, r.data);
        var player = Camera.PlayerPos;
        var cam = Camera.PosD;
        if (_clock >= _nextScan) { _nextScan = _clock + 0.25; Scan(player); }
        foreach (var it in _active.Values)
            if (it.Node != null) it.Node.Position = (it.Info.World - cam).ToVector3();
        _mat.SetShaderParameter("daylight", 1f - Night);
        UpdateHint();
    }

    void Scan(Vec3d player)
    {
        var q = GraphicsQuality.Current;
        double radius = Math.Max(q.InteriorRadius, BuildingsLayer.DoorOpenDistance + 8);
        _near.Clear();
        Buildings.InfosNear(player, Math.Max(radius, q.SignDistance), _near);
        _near.Sort((a, b) => a.dist.CompareTo(b.dist));

        // bâtiment où se trouve le joueur, et son étage
        _inside = null; _insideFloor = -1;
        double alt = player.Length() - Geo.Radius;
        foreach (var (b, d) in _near)
        {
            if (d > 0.5) break;
            var lp = Local(b, player);
            if (!Poly2.Inside(b.Ring, b.Holes, lp) || alt < b.Floor0 - 1.0 || alt > b.Top + 0.5) continue;
            if (_active.TryGetValue(b.Id, out var it) && it.Data != null) { _inside = it; _insideFloor = Math.Clamp((int)Math.Floor((alt - b.Floor0 + 0.6) / b.LevelH), 0, it.Data.AccessibleFloors - 1); }
            break;
        }
        PlayerInside = _inside != null;

        // intérieurs voulus : les plus proches, et toujours celui où est le joueur
        var wanted = new HashSet<string>();
        foreach (var (b, d) in _near)
        {
            if (d > radius || wanted.Count >= q.InteriorMax) break;
            wanted.Add(b.Id);
            int focus = _inside != null && _inside.Info.Id == b.Id ? _insideFloor : 0;
            if (!_active.TryGetValue(b.Id, out var it))
            {
                it = new Interior { Info = b, WantedFocus = focus };
                _active[b.Id] = it;
                StartBuild(it);
            }
            else
            {
                // tuile reconstruite (relief plus précis) : nouveau volume plein à désactiver, intérieur à refaire si le sol a bougé
                if (it.Info != b)
                {
                    bool moved = Math.Abs(it.Info.Floor0 - b.Floor0) > 0.05 || it.Info.Floors != b.Floors;
                    if (it.Data != null) it.Info.Solid.Solid = true;
                    it.Info = b;
                    if (it.Data != null) b.Solid.Solid = false;
                    if (moved && !it.Building) StartBuild(it);
                }
                it.WantedFocus = focus;
                // changement d'étage dans un immeuble : on meuble le nouvel étage
                if (it.Data != null && !it.Building && Math.Abs(it.Data.FocusFloor - focus) > GraphicsQuality.Current.InteriorFloorSpan / 2
                    && it.Data.AccessibleFloors > 1 && (it.Data.AccessibleFloors > 2 * q.InteriorFloorSpan + 1 || q.InteriorFloorSpan == 0))
                    StartBuild(it);
            }
        }
        if (_inside != null) wanted.Add(_inside.Info.Id);
        var remove = new List<string>();
        foreach (var (id, it) in _active) if (!wanted.Contains(id)) remove.Add(id);
        foreach (var id in remove) Drop(id);

        Signs(player, q.SignDistance);
    }

    static V2 Local(BuildingInfo b, Vec3d p)
    {
        var d = p.Normalized() - b.Frame.C;
        return new V2(d.Dot(b.Frame.East) * Geo.Radius, d.Dot(b.Frame.North) * Geo.Radius);
    }

    void StartBuild(Interior it)
    {
        it.Building = true;
        _building++;
        var info = it.Info; int focus = it.WantedFocus;
        var q = GraphicsQuality.Current;
        int detail = q.FurnitureDetail, span = q.InteriorFloorSpan;
        Task.Run(() =>
        {
            InteriorData data = null;
            try { data = InteriorBuilder.Build(info, detail, focus, span); }
            catch (Exception e) { GD.PrintErr($"[Intérieurs] {info.Id} : {e.Message}\n{e.StackTrace}"); }
            _done.Enqueue((it, data));
        });
    }

    void Install(Interior it, InteriorData data)
    {
        _building--;
        it.Building = false;
        if (it.Dead || data == null) return;
        // le bâtiment a changé entre-temps (tuile reconstruite) : on garde l'intérieur, il sera refait au prochain passage
        var mesh = new ArrayMesh();
        if (data.Indices.Length > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
            arrays[(int)Mesh.ArrayType.Color] = data.Colors;
            arrays[(int)Mesh.ArrayType.TexUV] = data.Uv;
            arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2;
            arrays[(int)Mesh.ArrayType.Index] = data.Indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        if (it.Node == null)
        {
            it.Node = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Planet.AddChild(it.Node);
        }
        it.Node.Mesh = mesh;
        it.Node.Position = (it.Info.World - Camera.PosD).ToVector3();
        Obstacles.Set(it.Node, data.Shapes);
        it.Data = data;
        it.Info.Solid.Solid = false; // on entre : les murs, planchers et escaliers de l'intérieur prennent le relais
        if (data.Info != it.Info) data.Info.Solid.Solid = true;
    }

    void Drop(string id)
    {
        if (!_active.Remove(id, out var it)) return;
        it.Dead = true;
        it.Info.Solid.Solid = true;
        it.Node?.QueueFree();
    }

    public override void _ExitTree()
    {
        foreach (var it in _active.Values) it.Info.Solid.Solid = true;
        PlayerInside = false;
    }

    // ------------------------------------------------------------------ enseignes

    void Signs(Vec3d player, double maxDist)
    {
        var keep = new HashSet<string>();
        int n = 0;
        foreach (var (b, d) in _near)
        {
            if (d > maxDist || n >= 60) break;
            string text = SignText(b);
            if (text == null) continue;
            keep.Add(b.Id); n++;
            if (_signs.ContainsKey(b.Id)) continue;
            var l = new Label3D
            {
                Text = text, FontSize = 64, PixelSize = 0.0042f, OutlineSize = 10,
                Modulate = new Color(1, 0.97f, 0.88f), OutlineModulate = new Color(0.08f, 0.06f, 0.05f, 0.9f),
                DoubleSided = false, Shaded = false,
            };
            var fr = b.Frame;
            var mid = (b.DoorA + b.DoorB) * 0.5 + b.DoorOut * 0.06;
            // devanture : au-dessus de l'auvent (sinon caché dessous) ; sinon juste au-dessus de la porte
            bool shopfront = b.Style is WindowStyle.ShopResidential or WindowStyle.ShopOffice;
            double h = shopfront ? b.Floor0 + Math.Min(b.LevelH, 3.4) + 0.32 : b.Floor0 + b.DoorH + 0.38;
            var w = fr.C * (Geo.Radius + h) + fr.East * mid.X + fr.North * mid.Y;
            // repère : X le long du mur (de gauche à droite vu de la rue), Y vers le haut, Z vers la rue
            var outw = (fr.East * b.DoorOut.X + fr.North * b.DoorOut.Y).ToVector3().Normalized();
            var up = fr.C.ToVector3();
            var right = up.Cross(outw).Normalized();
            l.Basis = new Basis(right, up, outw);
            l.Position = (w - Camera.PosD).ToVector3();
            Planet.AddChild(l);
            _signs[b.Id] = l;
            _signPos[b.Id] = w;
        }
        var drop = new List<string>();
        foreach (var id in _signs.Keys) if (!keep.Contains(id)) drop.Add(id);
        foreach (var id in drop) { _signs[id].QueueFree(); _signs.Remove(id); _signPos.Remove(id); }
    }

    readonly Dictionary<string, Vec3d> _signPos = new();

    static string SignText(BuildingInfo b)
    {
        string place = null;
        foreach (var (name, cat) in b.Places) { place = name; break; }
        if (b.Style is WindowStyle.ShopResidential or WindowStyle.ShopOffice || b.Kind is BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Restaurant)
            return place ?? b.Name;
        if (b.Kind is BuildingKind.Hotel or BuildingKind.School or BuildingKind.Kindergarten or BuildingKind.University or BuildingKind.Hospital
            or BuildingKind.TrainStation or BuildingKind.Library or BuildingKind.Museum or BuildingKind.FireStation)
            return b.Name ?? place;
        return null;
    }

    // ------------------------------------------------------------------ tests automatiques

    /// <summary>Test : place le personnage devant la porte du bâtiment le plus proche (de la fonction demandée,
    /// ex. « church », « school », « shop » ; « any » : n'importe lequel), tourné vers elle.</summary>
    public string TestDoor(string kind)
    {
        var list = new List<(BuildingInfo info, double dist)>();
        Buildings.InfosNear(Camera.PlayerPos, 3000, list);
        list.Sort((a, b) => a.dist.CompareTo(b.dist));
        foreach (var (b, _) in list)
        {
            if (kind != "any" && !string.Equals(b.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase)
                && !(kind == "shop" && b.Style is WindowStyle.ShopResidential or WindowStyle.ShopOffice)
                && !(kind == "tall" && b.Floors >= 5 && b.Area > 150)) continue;
            var fr = b.Frame;
            var mid = (b.DoorA + b.DoorB) * 0.5 + b.DoorOut * 3.0;
            var dirOut = fr.East * b.DoorOut.X + fr.North * b.DoorOut.Y;
            var feet = fr.C * (Geo.Radius + b.DoorGround) + fr.East * mid.X + fr.North * mid.Y;
            Geo.ToLatLon(feet.Normalized(), out double lat, out double lon);
            feet = feet.Normalized() * (Geo.Radius + Math.Max(HeightProvider.SurfaceHeight(lat, lon), b.DoorGround) + 0.05);
            Geo.LocalFrame(feet.Normalized(), out var e, out var n);
            double yaw = Math.Atan2(-dirOut.Dot(e), -dirOut.Dot(n));
            Camera.SetWalk(true);
            Camera.Body.PlaceAt(feet, yaw);
            Camera.Yaw = yaw;
            _nextScan = 0;
            return $"{Describe(b)} ({b.Floors} niveaux, {b.Area:N0} m², {b.Places.Count} lieux) à {lat:F6}, {lon:F6}";
        }
        return null;
    }

    /// <summary>Test : place le personnage au pied de l'escalier du bâtiment où il se trouve (ou du plus proche).</summary>
    public string TestStairs()
    {
        Interior best = null; double bd = double.MaxValue;
        foreach (var it in _active.Values)
        {
            if (it.Data?.Landings == null) continue;
            double d = (it.Info.Frame.C - Camera.PlayerPos.Normalized()).Length() * Geo.Radius;
            if (d < bd) { bd = d; best = it; }
        }
        if (best == null) return null;
        var d0 = best.Data;
        Geo.LocalFrame(d0.StairFoot.Normalized(), out var e, out var n);
        double yaw = Math.Atan2(d0.StairDir.Dot(e), d0.StairDir.Dot(n));
        Camera.Body.PlaceAt(d0.StairFoot, yaw);
        Camera.Yaw = yaw;
        return $"escalier de {Describe(best.Info)} ({d0.AccessibleFloors} niveaux, {d0.Rooms} pièces, {d0.Furniture} meubles)";
    }

    public bool HasInteriorNear(double maxDist)
    {
        foreach (var it in _active.Values)
            if (it.Data != null && (it.Info.Frame.C - Camera.PlayerPos.Normalized()).Length() * Geo.Radius - it.Info.BoundRadius < maxDist) return true;
        return false;
    }

    // ------------------------------------------------------------------ ascenseur, aide

    void UpdateHint()
    {
        // positions des enseignes (gardées en double, comme tout le monde : précises partout sur le globe)
        foreach (var (id, l) in _signs) if (_signPos.TryGetValue(id, out var w)) l.Position = (w - Camera.PosD).ToVector3();
        if (_inside == null || _inside.Data == null || !Camera.Walk) { _hint.Visible = false; return; }
        var d = _inside.Data;
        string lift = d.HasStairs && d.AccessibleFloors >= 5 ? "   ·   ascenseur : PgUp / PgDn" : "";
        string stairs = d.HasStairs ? "" : d.AccessibleFloors < _inside.Info.Floors ? "   (pas d'accès aux étages)" : "";
        _hint.Text = $"{char.ToUpper(Describe(_inside.Info)[0])}{Describe(_inside.Info)[1..]} — {FloorName(_insideFloor)}{stairs}{lift}";
        _hint.Visible = true;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.PhysicalKeycode is not (Key.Pageup or Key.Pagedown)) return;
        if (_inside?.Data is not InteriorData d || d.Landings == null || d.AccessibleFloors < 5 || !Camera.Walk || Camera.Body == null) return;
        int target = Math.Clamp(_insideFloor + (k.PhysicalKeycode == Key.Pageup ? 1 : -1), 0, d.AccessibleFloors - 1);
        if (target == _insideFloor) return;
        Camera.Body.PlaceAt(d.Landings[target], Camera.Body.Yaw);
        _insideFloor = target;
        _nextScan = 0;
        GetViewport().SetInputAsHandled();
    }

    // Intérieurs : couleurs figées (pas d'ombres du soleil : on est sous un toit), parquet et carrelage dessinés,
    // plafonniers lumineux ; la nuit, lumière chaude des lampes.
    const string Shader = @"
shader_type spatial;
render_mode cull_disabled, unshaded;
uniform float daylight = 1.0;
void fragment() {
    vec3 c = pow(COLOR.rgb, vec3(2.2));
    int mat = int(UV2.x + 0.5);
    if (mat == 2) {
        // parquet : lames de 15 cm décalées
        vec2 p = UV / vec2(1.2, 0.15);
        float row = floor(p.y);
        float plank = floor(p.x + fract(row * 0.37) * 3.0);
        float v = fract(sin(dot(vec2(row, plank), vec2(12.9898, 78.233))) * 43758.5453);
        c *= 0.85 + 0.25 * v;
        c *= 1.0 - 0.35 * (step(fract(p.y), 0.04) + step(fract(p.x + fract(row * 0.37) * 3.0), 0.01));
    } else if (mat == 3) {
        // carrelage de 50 cm
        vec2 f = fract(UV / 0.5);
        c *= 1.0 - 0.18 * (step(f.x, 0.03) + step(f.y, 0.03));
    } else if (mat == 4) {
        c *= 1.0;
    }
    vec3 light = mix(vec3(0.85, 0.72, 0.55), vec3(1.0), daylight);
    ALBEDO = c * light;
    if (mat == 1) ALBEDO = vec3(1.0, 0.95, 0.85) * mix(1.6, 1.0, daylight);
}";
}
