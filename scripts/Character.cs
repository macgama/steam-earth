using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Personnage jouable (mode marche, touche F) : un mannequin simple fait de capsules,
/// qui marche sur le relief avec une gravité dirigée vers le centre de la Terre.
///
/// La position réelle est en double (<see cref="PosD"/>, position des pieds dans le repère planète).
/// Comme tout le reste du monde, le nœud Godot est placé chaque image relativement à la caméra
/// (<see cref="Place"/>), ce qui garde la précision partout sur le globe.
/// </summary>
public partial class Character : Node3D
{
    public Vec3d PosD;           // pieds, repère planète
    public double Yaw;           // cap du corps (radians, 0 = nord, π/2 = est)
    public bool Grounded { get; private set; }
    public double GroundHeight { get; private set; }
    public double Speed { get; private set; } // vitesse horizontale actuelle (m/s)
    public double Altitude => PosD.Length() - Geo.Radius;

    public const double Height = 1.8;
    public const double JumpSpeed = 4.5;   // m/s → saut d'environ 1 m
    public const double Gravity = 9.81;

    double _verticalSpeed;
    double _phase;  // cycle de marche
    double _swing;  // amplitude actuelle du balancement (0 à l'arrêt)
    Node3D _legL, _legR, _armL, _armR;

    public override void _Ready()
    {
        var shirt = Mat(new Color(0.85f, 0.32f, 0.18f));
        var pants = Mat(new Color(0.16f, 0.22f, 0.38f));
        var skin = Mat(new Color(0.93f, 0.76f, 0.62f));
        var dark = Mat(new Color(0.12f, 0.12f, 0.12f));
        var bag = Mat(new Color(0.30f, 0.42f, 0.22f));

        // Godot : l'avant du personnage est −Z, le haut +Y, l'origine aux pieds.
        _legL = Limb(new Vector3(-0.11f, 0.92f, 0), 0.085f, 0.92f, pants);
        _legR = Limb(new Vector3(0.11f, 0.92f, 0), 0.085f, 0.92f, pants);
        _armL = Limb(new Vector3(-0.29f, 1.47f, 0), 0.06f, 0.68f, shirt);
        _armR = Limb(new Vector3(0.29f, 1.47f, 0), 0.06f, 0.68f, shirt);
        Part(new CapsuleMesh { Radius = 0.2f, Height = 0.72f }, shirt, new Vector3(0, 1.22f, 0));   // torse
        Part(new SphereMesh { Radius = 0.13f, Height = 0.26f }, skin, new Vector3(0, 1.67f, 0));    // tête
        Part(new BoxMesh { Size = new Vector3(0.2f, 0.05f, 0.05f) }, dark, new Vector3(0, 1.69f, -0.115f)); // yeux : montre l'avant
        Part(new BoxMesh { Size = new Vector3(0.3f, 0.38f, 0.16f) }, bag, new Vector3(0, 1.25f, 0.22f));    // sac à dos
    }

    static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.85f };

    MeshInstance3D Part(Mesh mesh, Material mat, Vector3 pos, Node3D parent = null)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos };
        (parent ?? this).AddChild(mi);
        return mi;
    }

    /// <summary>Membre articulé : pivot à l'épaule ou à la hanche, la capsule pend en dessous.</summary>
    Node3D Limb(Vector3 pivot, float radius, float length, Material mat)
    {
        var joint = new Node3D { Position = pivot };
        AddChild(joint);
        Part(new CapsuleMesh { Radius = radius, Height = length }, mat, new Vector3(0, -length / 2, 0), joint);
        return joint;
    }

    /// <summary>Pose le personnage sur le sol en (lat, lon).</summary>
    public void PlaceOnGround(double lat, double lon, double yaw)
    {
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        PosD = Geo.FromLatLon(lat, lon) * (Geo.Radius + GroundHeight);
        Yaw = yaw;
        _verticalSpeed = 0;
        Grounded = true;
    }

    /// <summary>Place le personnage à une position quelconque (il tombera s'il est en l'air).</summary>
    public void PlaceAt(Vec3d feet, double yaw)
    {
        PosD = feet;
        Yaw = yaw;
        _verticalSpeed = 0;
        Grounded = false;
    }

    /// <summary>
    /// Un pas de simulation : <paramref name="move"/> est une direction horizontale (repère planète,
    /// longueur 0 = immobile), <paramref name="speed"/> en m/s.
    /// </summary>
    public void Step(Vec3d move, double speed, bool jump, double dt)
    {
        var up = PosD.Normalized();
        Speed = 0;
        if (move.Length() > 1e-6)
        {
            var dir = move.Normalized();
            PosD += dir * (speed * dt);
            Speed = speed;
            // Le corps se tourne progressivement vers la direction de marche
            Geo.LocalFrame(up, out var east, out var north);
            double target = Math.Atan2(dir.Dot(east), dir.Dot(north));
            double diff = Math.IEEERemainder(target - Yaw, 2 * Math.PI);
            Yaw += diff * Math.Min(1, dt * 12);
        }

        if (jump && Grounded) { _verticalSpeed = JumpSpeed; Grounded = false; }
        _verticalSpeed -= Gravity * dt;
        PosD += PosD.Normalized() * (_verticalSpeed * dt);

        // Contrainte au sol (gravité radiale : « haut » = direction opposée au centre de la Terre)
        up = PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        double above = Altitude - GroundHeight;
        // On « colle » au sol dans les descentes plutôt que de décoller à chaque pas
        bool snap = Grounded && _verticalSpeed <= 0 && above < Math.Max(0.3, Speed * dt * 1.5);
        if (above < 0 || snap)
        {
            PosD = up * (Geo.Radius + GroundHeight);
            _verticalSpeed = 0;
            Grounded = true;
        }
        else if (above > 0.05) Grounded = false;

        Animate(dt);
    }

    void Animate(double dt)
    {
        // Balancement des bras et des jambes, proportionnel à la vitesse
        double wanted = Grounded ? Math.Min(1, Speed / 4.0) : 0.35;
        _swing += (wanted - _swing) * Math.Min(1, dt * 8);
        if (Grounded && Speed > 0) _phase += Speed * dt / 0.75 * Math.PI; // ~1,5 m par cycle
        float a = (float)(Math.Sin(_phase) * 0.65 * _swing);
        _legL.Rotation = new Vector3(a, 0, 0);
        _legR.Rotation = new Vector3(-a, 0, 0);
        _armL.Rotation = new Vector3(-a * 0.8f, 0, -0.08f);
        _armR.Rotation = new Vector3(a * 0.8f, 0, 0.08f);
    }

    /// <summary>Place le nœud Godot par rapport à la caméra (le monde est rendu relatif à elle).</summary>
    public void Place(Vec3d cameraPos, Basis worldRotation)
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var fwd = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var right = fwd.Cross(up).Normalized();
        var basis = new Basis(right.ToVector3(), up.ToVector3(), (-fwd).ToVector3());
        Transform = new Transform3D(worldRotation * basis, worldRotation * (PosD - cameraPos).ToVector3());
    }
}
