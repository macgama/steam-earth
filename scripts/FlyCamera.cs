using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Caméra libre, du sol à l'orbite. La position réelle est en double (<see cref="PosD"/>, repère planète) ;
/// le nœud Godot reste à l'origine (rendu relatif à la caméra).
/// Le cap et l'inclinaison sont relatifs à l'horizon local : « haut » = verticale du lieu.
///
/// Commandes : clic gauche = capturer la souris, Échap = libérer,
/// ZQSD / WASD = avancer, Espace / E = monter, C / A = descendre,
/// Maj = ×8, molette = multiplicateur de vitesse, 1–5 = lieux prédéfinis, F = marcher (collé au sol).
/// </summary>
public partial class FlyCamera : Camera3D
{
    public Vec3d PosD;
    public double Yaw;   // radians, 0 = nord, π/2 = est
    public double Pitch; // radians, + = vers le haut
    public double SpeedMultiplier = 1.0;
    public bool Walk;    // mode marche : altitude fixée à 1,7 m au-dessus du sol

    public double Altitude => PosD.Length() - Geo.Radius;
    public double GroundHeight { get; private set; }
    public double Speed { get; private set; }

    public override void _Ready()
    {
        Near = 0.25f;
        Far = 50_000_000f;
        Fov = 70;
    }

    public void Teleport(double lat, double lon, double heightAboveGround, double yawDeg, double pitchDeg)
    {
        double ground = HeightProvider.SurfaceHeight(lat, lon);
        PosD = Geo.FromLatLon(lat, lon) * (Geo.Radius + ground + heightAboveGround);
        Yaw = yawDeg * Math.PI / 180;
        Pitch = pitchDeg * Math.PI / 180;
        UpdateTransform();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left) Input.MouseMode = Input.MouseModeEnum.Captured;
            if (mb.ButtonIndex == MouseButton.WheelUp) SpeedMultiplier *= 1.25;
            if (mb.ButtonIndex == MouseButton.WheelDown) SpeedMultiplier /= 1.25;
        }
        else if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Yaw += mm.Relative.X * 0.0025;
            Pitch = Math.Clamp(Pitch - mm.Relative.Y * 0.0025, -1.55, 1.55);
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.PhysicalKeycode)
            {
                case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
                case Key.F: Walk = !Walk; break;
                case Key.Key1: Teleport(46.70, 7.80, 1500, 120, -12); break;   // Interlaken, vers l'Eiger
                case Key.Key2: Teleport(46.03, 7.70, 600, 200, 5); break;     // Zermatt, vers le Cervin
                case Key.Key3: Teleport(46.45, 6.55, 3000, 90, -10); break;    // Lac Léman
                case Key.Key4: Teleport(46.8, 8.2, 3_000_000, 0, -89); break;  // Europe depuis l'espace
                case Key.Key5: Teleport(27.9, 86.6, 2000, 90, 0); break;       // Himalaya (données basse résolution)
            }
        }
    }

    public override void _Process(double delta)
    {
        var up = PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        double agl = Altitude - GroundHeight;

        Geo.LocalFrame(up, out var east, out var north);
        var heading = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var forward = heading * Math.Cos(Pitch) + up * Math.Sin(Pitch);
        var right = heading.Cross(up).Normalized();

        var move = new Vec3d(0, 0, 0);
        if (Input.IsPhysicalKeyPressed(Key.W)) move += Walk ? heading : forward;   // W = Z en AZERTY (touche physique)
        if (Input.IsPhysicalKeyPressed(Key.S)) move -= Walk ? heading : forward;
        if (Input.IsPhysicalKeyPressed(Key.D)) move += right;
        if (Input.IsPhysicalKeyPressed(Key.A)) move -= right;                      // A = Q en AZERTY
        if (!Walk && (Input.IsPhysicalKeyPressed(Key.Space) || Input.IsPhysicalKeyPressed(Key.E))) move += up;
        if (!Walk && (Input.IsPhysicalKeyPressed(Key.C) || Input.IsPhysicalKeyPressed(Key.Q))) move -= up;

        double baseSpeed = Walk ? 5.0 : Math.Clamp(agl, 5, 5_000_000);
        if (Input.IsPhysicalKeyPressed(Key.Shift)) baseSpeed *= 8;
        Speed = baseSpeed * SpeedMultiplier;
        if (move.Length() > 0) PosD += move.Normalized() * (Speed * delta);

        // Contrainte au sol
        up = PosD.Normalized();
        Geo.ToLatLon(up, out lat, out lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        double minAlt = GroundHeight + 1.7;
        if (Walk || Altitude < minAlt) PosD = up * (Geo.Radius + minAlt);

        UpdateTransform();
    }

    /// <summary>Rotation planète → Godot : est → +X, haut → +Y, nord → −Z.</summary>
    public Basis WorldRotation { get; private set; } = Basis.Identity;

    void UpdateTransform()
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        WorldRotation = new Basis(east.ToVector3(), up.ToVector3(), (-north).ToVector3()).Transposed();
        // Dans le repère Godot, la caméra ne fait que tourner autour de la verticale locale
        var fwd = new Vector3((float)(Math.Sin(Yaw) * Math.Cos(Pitch)), (float)Math.Sin(Pitch), (float)(-Math.Cos(Yaw) * Math.Cos(Pitch)));
        Transform = new Transform3D(Basis.LookingAt(fwd, Vector3.Up), Vector3.Zero);

        // Plans de découpe adaptés à l'altitude (évite un rapport far/near démesuré)
        double h = Math.Max(1, Altitude - GroundHeight);
        double alt = Math.Max(0, Altitude);
        double horizon = Math.Sqrt(alt * (2 * Geo.Radius + alt)) + 400_000;
        Near = (float)Math.Clamp(h * 0.002, 0.1, 2000);
        Far = (float)Math.Min(horizon + Geo.Radius, 60_000_000);
    }
}
