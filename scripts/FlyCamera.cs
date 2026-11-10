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
/// Maj = ×8, molette = multiplicateur de vitesse, 1–9 = lieux prédéfinis,
/// F = marcher avec le personnage (gravité ; ZQSD pour marcher, Maj pour courir, Espace pour sauter),
/// V = vue arrière (troisième personne) / vue à la première personne, molette = distance de la caméra.
/// </summary>
public partial class FlyCamera : Camera3D
{
    public Vec3d PosD;
    public double Yaw;   // radians, 0 = nord, π/2 = est
    public double Pitch; // radians, + = vers le haut
    public double SpeedMultiplier = 1.0;
    public bool Walk;    // mode marche : on contrôle le personnage (Body)
    public Character Body;
    public bool ThirdPerson = true;  // vue arrière (sinon : vue par les yeux du personnage)
    public double CamDistance = 4.5; // distance caméra–personnage en vue arrière (m)

    /// <summary>Position du joueur : le personnage en mode marche, sinon la caméra.</summary>
    public Vec3d PlayerPos => Walk && Body != null ? Body.PosD : PosD;

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
        if (Walk && Body != null)
        {
            // En marche, on pose le personnage au sol et la caméra se place derrière lui
            Body.PlaceOnGround(lat, lon, yawDeg * Math.PI / 180);
            Yaw = Body.Yaw;
            Pitch = ThirdPersonPitch;
            FollowBody();
            return;
        }
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
            bool zoom = Walk && Body != null && ThirdPerson; // en vue arrière, la molette rapproche / éloigne la caméra
            if (mb.ButtonIndex == MouseButton.WheelUp) { if (zoom) CamDistance = Math.Max(1.5, CamDistance / 1.15); else SpeedMultiplier *= 1.25; }
            if (mb.ButtonIndex == MouseButton.WheelDown) { if (zoom) CamDistance = Math.Min(40, CamDistance * 1.15); else SpeedMultiplier /= 1.25; }
        }
        else if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Yaw += mm.Relative.X * 0.0025;
            // En vue arrière, on limite pour ne pas passer sous le personnage ni à la verticale
            bool tp = Walk && Body != null && ThirdPerson;
            Pitch = Math.Clamp(Pitch - mm.Relative.Y * 0.0025, tp ? -1.35 : -1.55, tp ? 0.7 : 1.55);
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.PhysicalKeycode)
            {
                case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
                case Key.F: SetWalk(!Walk); break;
                case Key.V: ThirdPerson = !ThirdPerson; if (ThirdPerson) Pitch = Math.Clamp(Pitch, -1.35, 0.7); break;
                case Key.Key1: Teleport(46.70, 7.80, 1500, 120, -12); break;   // Interlaken, vers l'Eiger
                case Key.Key2: Teleport(46.03, 7.70, 600, 200, 5); break;     // Zermatt, vers le Cervin
                case Key.Key3: Teleport(46.45, 6.55, 3000, 90, -10); break;    // Lac Léman
                case Key.Key4: Teleport(46.8, 8.2, 3_000_000, 0, -89); break;  // Europe depuis l'espace
                case Key.Key5: Teleport(27.9, 86.6, 2000, 90, 0); break;       // Himalaya (données basse résolution)
                // Grandes villes, pour comparer jour/nuit et fuseaux (relief basse résolution)
                case Key.Key6: Teleport(40.70, -74.02, 800, 30, -10); break;   // New York
                case Key.Key7: Teleport(35.68, 139.70, 800, 270, -5); break;   // Tokyo, vers le mont Fuji
                case Key.Key8: Teleport(-33.86, 151.21, 800, 0, -10); break;   // Sydney
                case Key.Key9: Teleport(-22.95, -43.21, 800, 180, -10); break; // Rio de Janeiro
            }
        }
    }

    // --- Marche
    public const double EyeHeight = 1.7;
    public const double WalkSpeed = 4.0;    // m/s (petit trot)
    public const double SprintSpeed = 9.0;  // m/s avec Maj
    public const double ThirdPersonPitch = -0.3; // caméra légèrement au-dessus du personnage
    public const double LookHeight = 1.55;       // la caméra vise la tête du personnage
    public bool Grounded { get; private set; }
    public bool AutoForward;  // test automatique : avance seul (argument --autowalk)
    public bool AutoJump;     // test automatique : tient Espace (argument --climb)
    public bool InputBlocked; // menu ouvert : les touches servent à l'interface

    public override void _Process(double delta)
    {
        if (Walk && Body != null) { ProcessWalk(delta); return; }

        var up = PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        double agl = Altitude - GroundHeight;

        Geo.LocalFrame(up, out var east, out var north);
        var heading = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var forward = heading * Math.Cos(Pitch) + up * Math.Sin(Pitch);
        var right = heading.Cross(up).Normalized();
        bool shift = (!InputBlocked && Input.IsPhysicalKeyPressed(Key.Shift)) || AutoForward;

        var move = new Vec3d(0, 0, 0);
        bool keys = !InputBlocked;
        if (keys && Input.IsPhysicalKeyPressed(Key.W) || AutoForward) move += forward;   // W = Z en AZERTY (touche physique)
        if (keys && Input.IsPhysicalKeyPressed(Key.S)) move -= forward;
        if (keys && Input.IsPhysicalKeyPressed(Key.D)) move += right;
        if (keys && Input.IsPhysicalKeyPressed(Key.A)) move -= right;                      // A = Q en AZERTY

        if (keys && (Input.IsPhysicalKeyPressed(Key.Space) || Input.IsPhysicalKeyPressed(Key.E))) move += up;
        if (keys && (Input.IsPhysicalKeyPressed(Key.C) || Input.IsPhysicalKeyPressed(Key.Q))) move -= up;
        double baseSpeed = Math.Clamp(agl, 5, 5_000_000);
        if (shift) baseSpeed *= 8;
        Speed = baseSpeed * SpeedMultiplier;
        if (move.Length() > 0) PosD += move.Normalized() * (Speed * delta);

        // Contrainte au sol : les yeux jamais sous 1,7 m au-dessus du relief
        up = PosD.Normalized();
        Geo.ToLatLon(up, out lat, out lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        double eye = GroundHeight + EyeHeight;
        double above = Altitude - eye;
        if (above < 0) PosD = up * (Geo.Radius + eye);
        Grounded = above < 0.05;
        // Décors : la caméra ne rentre pas dans les bâtiments ni les troncs. Arrivée par le haut : posée sur le
        // toit ; sinon repoussée hors des murs.
        if (agl < 300 && Obstacles.Inside(PosD, 0.3, out double roof))
        {
            double alt = PosD.Length() - Geo.Radius;
            if (alt > roof - 3) PosD = up * (Geo.Radius + roof + 0.3 + EyeHeight * 0.2);
            else PosD = Obstacles.Resolve(PosD - up * 0.3, 0.4, 0.6) + up * 0.3;
        }

        UpdateTransform();
    }

    /// <summary>Mode marche : le clavier déplace le personnage, la caméra le suit (vue arrière) ou voit par ses yeux.</summary>
    void ProcessWalk(double delta)
    {
        bool keys = !InputBlocked;
        var up = Body.PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        // Les directions suivent la caméra : Z avance « droit devant » à l'écran
        var heading = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var right = heading.Cross(up).Normalized();
        var move = new Vec3d(0, 0, 0);
        if (keys && Input.IsPhysicalKeyPressed(Key.W) || AutoForward) move += heading;   // W = Z en AZERTY
        if (keys && Input.IsPhysicalKeyPressed(Key.S)) move -= heading;
        if (keys && Input.IsPhysicalKeyPressed(Key.D)) move += right;
        if (keys && Input.IsPhysicalKeyPressed(Key.A)) move -= right;                     // A = Q en AZERTY
        bool sprint = (keys && Input.IsPhysicalKeyPressed(Key.Shift)) || AutoForward;
        bool jump = (keys && Input.IsPhysicalKeyPressed(Key.Space)) || (AutoJump && AutoForward);

        Body.Step(move, sprint ? SprintSpeed : WalkSpeed, jump, delta);
        Speed = Body.Speed;
        Grounded = Body.Grounded;
        FollowBody();
    }

    /// <summary>Place la caméra derrière le personnage (ou dans ses yeux) puis positionne le personnage à l'écran.</summary>
    void FollowBody()
    {
        var up = Body.PosD.Normalized();
        Vec3d? target = null;
        if (ThirdPerson)
        {
            Geo.LocalFrame(up, out var east, out var north);
            var heading = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
            var look = heading * Math.Cos(Pitch) + up * Math.Sin(Pitch);
            var head = Body.PosD + up * LookHeight;
            // Si une pente se trouve entre le personnage et la caméra, on rapproche la caméra
            double dist = CamDistance;
            const int samples = 24; // pas fin : les murs et les troncs sont minces
            for (int i = 1; i <= samples; i++)
            {
                double d = CamDistance * i / samples;
                var p = head - look * d;
                if (AboveGround(p) < 0.4 || Obstacles.Inside(p, 0.25, out _)) { dist = Math.Max(0.6, CamDistance * (i - 1) / samples); break; }
            }
            PosD = head - look * dist;
            double above = AboveGround(PosD);
            if (above < 0.4) PosD = PosD.Normalized() * (PosD.Length() + 0.4 - above);
            target = head;
        }
        else PosD = Body.PosD + up * EyeHeight;
        Body.Visible = ThirdPerson;

        Geo.ToLatLon(PosD.Normalized(), out double lat, out double lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        UpdateTransform(target);
        Body.Place(PosD, WorldRotation);
    }

    static double AboveGround(Vec3d p)
    {
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        double ground = Math.Max(HeightProvider.SurfaceHeight(lat, lon), Obstacles.SurfaceBelow(p, 1.0));
        return p.Length() - Geo.Radius - ground;
    }

    /// <summary>
    /// Passe en marche (F) : le personnage apparaît sous la caméra. Proche du sol, il tombe depuis la caméra ;
    /// plus haut, il est posé directement au sol (pas de chute de plusieurs kilomètres).
    /// En quittant la marche, on reprend le vol depuis la position actuelle de la caméra.
    /// </summary>
    public void SetWalk(bool walk)
    {
        Walk = walk;
        if (Body == null) return;
        Body.Visible = false;
        if (!walk) { Speed = 0; return; }
        var up = PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        double agl = Altitude - HeightProvider.SurfaceHeight(lat, lon);
        if (agl > 30) Body.PlaceOnGround(lat, lon, Yaw);
        else Body.PlaceAt(up * (PosD.Length() - EyeHeight), Yaw);
        Pitch = ThirdPerson ? ThirdPersonPitch : 0;
        FollowBody();
    }

    /// <summary>Rotation planète → Godot : est → +X, haut → +Y, nord → −Z.</summary>
    public Basis WorldRotation { get; private set; } = Basis.Identity;

    /// <param name="lookAt">point visé (repère planète) ; sinon, la direction donnée par le cap et l'inclinaison.</param>
    void UpdateTransform(Vec3d? lookAt = null)
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        WorldRotation = new Basis(east.ToVector3(), up.ToVector3(), (-north).ToVector3()).Transposed();
        // Dans le repère Godot, la caméra ne fait que tourner autour de la verticale locale
        var fwd = new Vector3((float)(Math.Sin(Yaw) * Math.Cos(Pitch)), (float)Math.Sin(Pitch), (float)(-Math.Cos(Yaw) * Math.Cos(Pitch)));
        if (lookAt is Vec3d target)
        {
            var toTarget = WorldRotation * (target - PosD).ToVector3();
            if (toTarget.Length() > 0.01 && Math.Abs(toTarget.Normalized().Y) < 0.995) fwd = toTarget.Normalized();
        }
        Transform = new Transform3D(Basis.LookingAt(fwd, Vector3.Up), Vector3.Zero);

        // Plans de découpe adaptés à l'altitude (évite un rapport far/near démesuré)
        double h = Math.Max(1, Altitude - GroundHeight);
        double alt = Math.Max(0, Altitude);
        double horizon = Math.Sqrt(alt * (2 * Geo.Radius + alt)) + 400_000;
        Near = (float)Math.Clamp(h * 0.002, 0.1, 2000);
        Far = (float)Math.Min(horizon * 1.1 + 50_000, 60_000_000);
    }
}
