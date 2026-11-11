using System;
using Godot;

namespace SteamEarth;

/// <summary>Commandes du joueur pour un véhicule (clavier), valeurs de −1 à 1.</summary>
public struct VehicleInput
{
    public double Throttle;  // Z (+1) / S (−1)
    public double Steer;     // Q (−1, gauche) / D (+1, droite)
    public double Rudder;    // ← / → (avion : palonnier)
    public bool Boost;       // Maj
    public bool Brake;       // Espace (frein à main, freins de roues)
    public bool ThrottleUp, ThrottleDown; // avion : Maj / Ctrl (manette des gaz)
}

public enum VehicleKind { Car, Bike, Boat, Plane }

/// <summary>
/// Véhicule jouable sur la Terre ronde. Comme le personnage, la position réelle est en double dans le
/// repère planète (<see cref="PosD"/> = point de contact avec le sol ou l'eau), la gravité est dirigée vers
/// le centre de la Terre et le nœud Godot est replacé chaque image par rapport à la caméra (<see cref="Place"/>).
/// L'orientation est donnée par deux vecteurs du repère planète : l'avant (<see cref="Fwd"/>) et le haut (<see cref="Up"/>).
/// </summary>
public abstract partial class Vehicle : Node3D
{
    public const double G = 9.81;

    public Vec3d PosD;
    public Vec3d Fwd = new(0, 0, -1), Up = new(0, 1, 0);
    public Vec3d Right => Fwd.Cross(Up).Normalized();

    public abstract VehicleKind Kind { get; }
    public abstract string Label { get; }
    /// <summary>Vitesse affichée (m/s).</summary>
    public abstract double Speed { get; }
    /// <summary>Peut-on descendre maintenant (à l'arrêt, posé) ?</summary>
    public abstract bool CanExit { get; }
    /// <summary>Rayon du véhicule pour les collisions (m).</summary>
    public abstract double Radius { get; }
    public virtual double BodyHeight => 1.5;
    /// <summary>Emprise au sol une fois garé (demi-longueur, demi-largeur, hauteur en m), obstacle pour le personnage ;
    /// demi-longueur 0 = pas d'obstacle.</summary>
    public virtual (double halfLength, double halfWidth, double height) Footprint => (0, 0, 0);
    /// <summary>Point visé par la caméra, au-dessus du point de contact (m).</summary>
    public virtual double CameraTargetHeight => 1.3;
    public virtual double CameraDistance => 7;
    public virtual double CameraPitch => -0.22;
    /// <summary>Le joueur est assis dedans ou dessus (affiche le conducteur).</summary>
    public bool Occupied { get; set; }
    /// <summary>Message ponctuel à afficher (crash, échouage…), lu et vidé par le gestionnaire.</summary>
    public string Notice;

    /// <summary>Vent réel (m/s, repère planète) à une position et une hauteur au-dessus du sol ; fourni par la météo.</summary>
    public static Func<Vec3d, double, Vec3d> Wind = (p, h) => new Vec3d(0, 0, 0);

    public abstract void Step(VehicleInput input, double dt);
    /// <summary>Ligne d'informations pour le HUD.</summary>
    public abstract string Hud();

    /// <summary>Pose le véhicule au sol (ou sur l'eau) au point <paramref name="pos"/>, cap <paramref name="heading"/> (tangent).</summary>
    public virtual void PlaceAt(Vec3d pos, Vec3d heading)
    {
        var up = pos.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        PosD = up * (Geo.Radius + Ground(lat, lon, out _));
        Up = up;
        Fwd = Tangent(heading, up);
    }

    /// <summary>Cap (radians, 0 = nord, π/2 = est) de l'avant du véhicule.</summary>
    public double HeadingYaw
    {
        get
        {
            var up = PosD.Normalized();
            Geo.LocalFrame(up, out var east, out var north);
            var f = Tangent(Fwd, up);
            return Math.Atan2(f.Dot(east), f.Dot(north));
        }
    }

    /// <summary>Hauteur au-dessus du relief (m).</summary>
    public double HeightAboveGround
    {
        get
        {
            Geo.ToLatLon(PosD.Normalized(), out double lat, out double lon);
            return PosD.Length() - Geo.Radius - HeightProvider.SurfaceHeight(lat, lon);
        }
    }

    /// <summary>Endroit où le personnage descend : à gauche du véhicule.</summary>
    public virtual Vec3d ExitPoint => PosD - Right * (Radius + 0.8);

    public Vec3d CameraTarget => PosD + PosD.Normalized() * CameraTargetHeight;

    /// <summary>Place le nœud Godot par rapport à la caméra (le monde est rendu relatif à elle).</summary>
    public void Place(Vec3d cameraPos, Basis worldRotation)
    {
        var r = Right;
        var basis = new Basis(r.ToVector3(), Up.ToVector3(), (-Fwd).ToVector3());
        Transform = new Transform3D(worldRotation * basis, worldRotation * (PosD - cameraPos).ToVector3());
        Animate();
    }

    protected virtual void Animate() { }

    // --- Outils communs

    /// <summary>Surface praticable (m) et présence d'eau (lac, rivière, mer) en (lat, lon).</summary>
    public static double Ground(double lat, double lon, out bool water)
    {
        float h = HeightProvider.Sample(lat, lon, 0.5, out water, out _);
        return water ? Math.Max(h, 0) : h;
    }

    public static double Ground(Vec3d p, out bool water)
    {
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        return Ground(lat, lon, out water);
    }

    /// <summary>Projette <paramref name="v"/> dans le plan horizontal local et le normalise.</summary>
    public static Vec3d Tangent(Vec3d v, Vec3d up)
    {
        var t = v - up * v.Dot(up);
        if (t.Length() < 1e-9) { Geo.LocalFrame(up, out _, out var north); return north; }
        return t.Normalized();
    }

    /// <summary>Tourne <paramref name="v"/> autour de l'axe unitaire <paramref name="axis"/> (règle de la main droite).</summary>
    public static Vec3d Rotate(Vec3d v, Vec3d axis, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + axis.Cross(v) * s + axis * (axis.Dot(v) * (1 - c));
    }

    protected static StandardMaterial3D Mat(Color c, float rough = 0.6f, float metal = 0) =>
        new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    protected MeshInstance3D Part(Mesh mesh, Material mat, Vector3 pos, Node3D parent = null, Vector3? rot = null)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos };
        if (rot is Vector3 r) mi.Rotation = r;
        (parent ?? this).AddChild(mi);
        return mi;
    }

    protected Node3D Pivot(Vector3 pos, Node3D parent = null)
    {
        var n = new Node3D { Position = pos };
        (parent ?? this).AddChild(n);
        return n;
    }

    /// <summary>Roue : pivot (direction, rotation Y) → moyeu (rotation X) → pneu et jante (axe le long de X).</summary>
    protected Node3D Wheel(Vector3 pos, float radius, float width, Material tyre, Material rim, Node3D parent, out Node3D hub)
    {
        var pivot = Pivot(pos, parent);
        hub = Pivot(Vector3.Zero, pivot);
        var axis = new Vector3(0, 0, Mathf.Pi / 2);
        Part(new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = width, RadialSegments = 18 }, tyre, Vector3.Zero, hub, axis);
        Part(new CylinderMesh { TopRadius = radius * 0.55f, BottomRadius = radius * 0.55f, Height = width * 1.05f, RadialSegments = 12 }, rim, Vector3.Zero, hub, axis);
        Part(new BoxMesh { Size = new Vector3(width * 1.1f, radius * 1.1f, radius * 0.18f) }, tyre, Vector3.Zero, hub); // rayon : rend la rotation visible
        return pivot;
    }

    Node3D _riderRoot;
    CharacterModel _riderModel;
    CharacterSeat _riderSeat;

    /// <summary>Place du conducteur : le personnage du joueur (même apparence et tenue) y apparaît assis quand
    /// le véhicule est occupé. <paramref name="seat"/> = dessus du siège, repère du véhicule.</summary>
    protected Node3D Rider(Vector3 seat, CharacterSeat kind, Node3D parent = null)
    {
        _riderRoot = Pivot(seat, parent);
        _riderRoot.Visible = false;
        _riderSeat = kind;
        return _riderRoot;
    }

    /// <summary>Anime le conducteur (appelé à chaque image par <see cref="Animate"/>).</summary>
    protected void UpdateRider(double pedal = 0, double lean = 0)
    {
        if (_riderRoot == null) return;
        _riderRoot.Visible = Occupied;
        if (!Occupied) return;
        if (_riderModel == null)
        {
            // créé à la première montée seulement (les véhicules garés n'ont pas de conducteur)
            _riderModel = new CharacterModel { Name = "Driver", IsPlayer = true };
            _riderRoot.AddChild(_riderModel);
        }
        var m = new CharacterMotion { Seat = _riderSeat, Grounded = true, Pedal = pedal, Lean = lean };
        _riderModel.Update(m, GetProcessDeltaTime());
        // le bassin repose sur le siège
        var pelvis = _riderModel.PelvisLocal;
        _riderModel.Position = new Vector3(-pelvis.X, 0.1f - pelvis.Y, -pelvis.Z + 0.05f);
    }
}
