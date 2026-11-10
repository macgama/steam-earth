using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth;

/// <summary>
/// Affichage discret des frontières (lignes fines posées sur le relief) et des capitales
/// (étiquettes visibles à moins de ~800 km). Touche B : afficher / masquer.
/// </summary>
public partial class PoliticalLayer : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public bool ShowLayer = true;

    const double SegmentMax = 300;        // m : les frontières sont redécoupées pour épouser le relief
    const double LabelMaxDistance = 800_000;

    sealed class Cell { public Vec3d Origin; public MeshInstance3D Mesh; }
    readonly List<Cell> _cells = new();
    readonly List<(Vec3d pos, Label3D label)> _capitals = new();
    readonly List<Country> _capitalCountries = new();
    ShaderMaterial _lineMat;

    public override void _Ready()
    {
        _lineMat = new ShaderMaterial { Shader = new Shader { Code = LineShader } };

        // Capitales
        foreach (var c in Geography.Countries)
        {
            double ground = HeightProvider.SurfaceHeight(c.Lat, c.Lon, 500);
            var pos = Geo.FromLatLon(c.Lat, c.Lon) * (Geo.Radius + ground + 60);
            var label = new Label3D
            {
                Text = "◆ " + c.Capital,
                FixedSize = true,
                PixelSize = 0.0009f,
                FontSize = 30,
                OutlineSize = 8,
                Modulate = new Color(1, 0.97f, 0.88f, 0.85f),
                OutlineModulate = new Color(0, 0, 0, 0.6f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Visible = false,
            };
            Planet.AddChild(label);
            _capitals.Add((pos, label));
            _capitalCountries.Add(c);
        }

        // Frontières : construites en arrière-plan (échantillonnage du relief)
        var borders = Geography.Borders;
        Task.Run(() =>
        {
            var built = BuildBorderMeshes(borders);
            CallDeferred(nameof(AddBorderMeshes), built);
        });
    }

    /// <summary>Capitales affichées, pour l'invite « on construit ? » (<see cref="BuildingsLayer"/>).</summary>
    public void CollectTargets(List<BuildingsLayer.Target> list)
    {
        for (int i = 0; i < _capitals.Count; i++)
        {
            var c = _capitalCountries[i];
            if (_capitals[i].label.Visible)
                list.Add(new BuildingsLayer.Target(c.Capital, c.Lat, c.Lon, BuildingsLayer.RadiusFor("capital", 0), BuildingsLayer.MaxRadiusFor("capital", 0), _capitals[i].label));
        }
    }

    /// <summary>Regroupe les lignes par cases de 10° et calcule les sommets posés sur le relief.</summary>
    static Godot.Collections.Array BuildBorderMeshes(List<double[]> borders)
    {
        var cells = new Dictionary<int, List<Vec3d>>();
        foreach (var line in borders)
        {
            int key = (int)Math.Floor((line[0] + 90) / 10) * 36 + (int)Math.Floor((line[1] + 180) / 10);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Vec3d>();
            Vec3d prev = default;
            for (int i = 0; i < line.Length / 2; i++)
            {
                var d = Geo.FromLatLon(line[2 * i], line[2 * i + 1]);
                if (i > 0)
                {
                    double len = (d - prev).Length() * Geo.Radius;
                    int steps = Math.Max(1, (int)Math.Ceiling(len / SegmentMax));
                    var a = Draped(prev);
                    for (int s = 1; s <= steps; s++)
                    {
                        var b = Draped((prev * (1 - (double)s / steps) + d * ((double)s / steps)).Normalized());
                        list.Add(a); list.Add(b); // paire = un segment
                        a = b;
                    }
                }
                prev = d;
            }
        }
        var result = new Godot.Collections.Array();
        foreach (var (key, pts) in cells)
        {
            if (pts.Count == 0) continue;
            var o = new Vec3d(0, 0, 0);
            foreach (var p in pts) o += p;
            var origin = o.Normalized() * Geo.Radius;
            var verts = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) verts[i] = (pts[i] - origin).ToVector3();
            result.Add(new Godot.Collections.Array { origin.X, origin.Y, origin.Z, verts });
        }
        return result;
    }

    static Vec3d Draped(Vec3d dir)
    {
        Geo.ToLatLon(dir, out double lat, out double lon);
        return dir * (Geo.Radius + HeightProvider.SurfaceHeight(lat, lon, 500) + 4);
    }

    void AddBorderMeshes(Godot.Collections.Array built)
    {
        foreach (var item in built)
        {
            var a = (Godot.Collections.Array)item;
            var verts = (Vector3[])a[3];
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _lineMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Planet.AddChild(mi);
            _cells.Add(new Cell { Origin = new Vec3d((double)a[0], (double)a[1], (double)a[2]), Mesh = mi });
        }
        GD.Print($"[PoliticalLayer] frontières prêtes ({_cells.Count} cases)");
    }

    public bool Ready => _cells.Count > 0;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.B) ShowLayer = !ShowLayer;
    }

    public override void _Process(double delta)
    {
        var cam = Camera.PosD;
        double alt = Camera.Altitude;
        foreach (var c in _cells)
        {
            c.Mesh.Visible = ShowLayer;
            if (ShowLayer) c.Mesh.Position = (c.Origin - cam).ToVector3();
        }
        // Les lignes s'estompent un peu depuis l'espace
        _lineMat.SetShaderParameter("opacity", (float)(alt > 200_000 ? 0.35 : 0.5));

        foreach (var (pos, label) in _capitals)
        {
            double d = (pos - cam).Length();
            bool vis = ShowLayer && d < LabelMaxDistance;
            label.Visible = vis;
            if (!vis) continue;
            label.Position = (pos - cam).ToVector3();
            float fade = (float)Math.Clamp((LabelMaxDistance - d) / 200_000, 0, 1);
            label.Modulate = new Color(1, 0.97f, 0.88f, 0.85f * fade);
            label.OutlineModulate = new Color(0, 0, 0, 0.6f * fade);
        }
    }

    const string LineShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, fog_disabled;
uniform float opacity = 0.5;
void vertex() {
    // Rapproche légèrement chaque sommet de la caméra pour que la ligne ne disparaisse pas
    // sous un relief affiché plus grossièrement que celui sur lequel elle est posée.
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    float d = length(v.xyz);
    float pull = min(0.003 * d + 6.0, 0.2 * d); // borné : un sommet ne doit jamais arriver sur la caméra
    v.xyz *= 1.0 - pull / max(d, 0.001);
    POSITION = PROJECTION_MATRIX * v;
}
void fragment() {
    ALBEDO = vec3(1.0, 0.95, 0.75);
    ALPHA = opacity;
}";
}
