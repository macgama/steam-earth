using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>Commandes du joueur pour un véhicule (clavier), valeurs de −1 à 1.</summary>
public struct VehicleInput
{
    public double Throttle;  // Z / ↑ (+1), S / ↓ (−1)
    public double Steer;     // Q (−1, gauche) / D (+1, droite)
    public double Rudder;    // ← / → (avion : palonnier ; hélicoptère : glisser de côté)
    public bool Boost;       // Maj (vélo : en danseuse)
    public bool Brake;       // Espace (frein à main, freins de roues)
    public bool ThrottleUp, ThrottleDown; // Maj / Ctrl : gaz de l'avion, monter / descendre en hélicoptère
}

/// <summary>Pièces animées d'un modèle 3D (roues, direction, hélices, gouvernes…), remplies par <see cref="VehicleModels"/>.</summary>
public sealed class VehicleRig
{
    public readonly List<Node3D> SteerPivots = new();
    public readonly List<(Node3D hub, float radius)> Wheels = new();
    public readonly List<(Node3D node, Vector3 axis, float factor)> Spinners = new(); // tournent avec le moteur
    public Node3D Lean, Rider, Boom, Elevator, AileronL, AileronR, RudderNode, Motor;
    /// <summary>Posture du conducteur sur le siège <see cref="Rider"/> (le personnage du joueur y est assis).</summary>
    public CharacterSeat Seat = CharacterSeat.Car;
    public readonly List<Node3D> LegsL = new(), LegsR = new();
    public readonly List<MeshInstance3D> BrakeLights = new();
}

/// <summary>
/// Véhicule jouable sur la Terre ronde. La position réelle est en double dans le repère planète
/// (<see cref="PosD"/> = point de contact avec le sol ou l'eau), la gravité est dirigée vers le centre de la Terre et
/// le nœud Godot est replacé chaque image par rapport à la caméra (<see cref="Place"/>).
/// L'orientation est donnée par deux vecteurs du repère planète : l'avant (<see cref="Fwd"/>) et le haut (<see cref="Up"/>).
/// La physique (arcade, facile au clavier) est dans les sous-classes ; elles remplissent aussi l'état commun
/// (régime, rapport, dérapage…) que lisent le son (<see cref="VehicleAudio"/>) et le tableau de bord.
/// </summary>
public abstract partial class Vehicle : Node3D
{
    public const double G = 9.81;

    public readonly VehicleSpec Spec;
    public Vec3d PosD;
    public Vec3d Fwd = new(0, 0, -1), Up = new(0, 1, 0);
    public Vec3d Right => Fwd.Cross(Up).Normalized();
    protected readonly VehicleRig Rig = new();

    protected Vehicle(VehicleSpec spec) { Spec = spec; Name = spec.Name; }

    public string Label => Spec.Name;
    /// <summary>Vitesse affichée (m/s) : le long de l'axe pour les véhicules terrestres et les bateaux, vitesse air pour les avions.</summary>
    public abstract double Speed { get; }
    /// <summary>Peut-on descendre maintenant (à l'arrêt, posé) ?</summary>
    public abstract bool CanExit { get; }
    public virtual double Radius => Spec.Width / 2;
    public virtual double BodyHeight => Spec.Height;
    public virtual double CameraTargetHeight => Spec.CamHeight;
    public virtual double CameraDistance => Spec.CamDistance;
    public virtual double CameraPitch => Spec.CamPitch;
    /// <summary>Emprise au sol une fois garé (demi-longueur, demi-largeur, hauteur), obstacle pour le personnage ; 0 = aucun.</summary>
    public virtual (double halfLength, double halfWidth, double height) Footprint =>
        Spec.OnWater || Spec.Family == VehicleFamily.Jet ? (0, 0, 0) : (Spec.Length / 2, Spec.Width / 2, Math.Min(Spec.Height, 2.2));

    /// <summary>Le joueur est dedans : conducteur visible (vue arrière), moteur en marche, son.</summary>
    public bool Occupied { get; set; }
    /// <summary>Vue à la première personne : on cache le conducteur (la caméra est à sa place).</summary>
    public bool FirstPerson { get; set; }
    /// <summary>Message ponctuel à afficher (crash, échouage…), lu et vidé par le gestionnaire.</summary>
    public string Notice;
    /// <summary>Un choc vient d'avoir lieu (force 0..1), lu et remis à zéro par le son.</summary>
    public double Impact;

    // --- État commun (son, tableau de bord, animation)
    public double Rpm01;        // régime moteur 0 (ralenti) .. 1 (maxi)
    public int Gear = 1;        // rapport engagé (0 = point mort, −1 = marche arrière)
    public double Throttle01;   // pédale / manette des gaz 0..1
    public double Slip;         // dérapage 0..1 (crissement des pneus)
    public bool Braking;
    public double SteerAngle;   // braquage des roues avant (rad, + = droite)
    public double WheelAngle;   // rotation cumulée des roues (rad)
    public double Lean;         // inclinaison des deux-roues dans les virages (rad, + = droite)
    public double Spin;         // rotation cumulée des hélices / rotors (rad)
    public double ElevatorIn, AileronIn, RudderIn; // gouvernes (−1..1)
    public double SailAngle;    // bôme du voilier (rad, + = vers tribord)
    public bool Grounded = true;

    /// <summary>Vent réel (m/s, repère planète) à une position et une hauteur au-dessus du sol ; fourni par la météo.</summary>
    public static Func<Vec3d, double, Vec3d> Wind = (p, h) => new Vec3d(0, 0, 0);

    public abstract void Step(VehicleInput input, double dt);
    /// <summary>Ligne d'informations pour le HUD, suivie du rappel des touches entre crochets.</summary>
    public abstract string Hud();

    public override void _Ready()
    {
        VehicleModels.Build(this, Spec, Rig);
    }

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

    /// <summary>Assiette (rad, + = nez en haut) et inclinaison (rad, + = aile droite basse) par rapport à l'horizon local.</summary>
    public double PitchAngle => Math.Asin(Math.Clamp(Fwd.Dot(PosD.Normalized()), -1, 1));
    public double BankAngle => -Math.Asin(Math.Clamp(Right.Dot(PosD.Normalized()), -1, 1));

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

    /// <summary>Position des yeux du conducteur (vue à la première personne), repère planète.</summary>
    public Vec3d EyePos => PosD + Right * Spec.Eye.X + Up * Spec.Eye.Y + Fwd * Spec.Eye.Z;

    /// <summary>Place le nœud Godot par rapport à la caméra (le monde est rendu relatif à elle).</summary>
    public void Place(Vec3d cameraPos, Basis worldRotation)
    {
        var basis = new Basis(Right.ToVector3(), Up.ToVector3(), (-Fwd).ToVector3());
        Transform = new Transform3D(worldRotation * basis, worldRotation * (PosD - cameraPos).ToVector3());
        Animate();
    }

    /// <summary>Anime les pièces du modèle à partir de l'état commun.</summary>
    protected virtual void Animate()
    {
        UpdateRider();
        if (FirstPerson != _shellHidden) HideShell(FirstPerson);
        // chaque roue tourne selon son rayon (les petites roues avant du tracteur tournent plus vite)
        foreach (var (hub, radius) in Rig.Wheels)
            hub.Rotation = new Vector3(-(float)(WheelAngle * Spec.WheelRadius / radius % (2 * Math.PI)), 0, 0);
        foreach (var p in Rig.SteerPivots) p.Rotation = new Vector3(0, -(float)SteerAngle, 0);
        float s = (float)(Spin % (2 * Math.PI));
        foreach (var (node, axis, factor) in Rig.Spinners) node.Basis = new Basis(axis, s * factor);
        if (Rig.Lean != null) Rig.Lean.Rotation = new Vector3(0, 0, -(float)Lean);
        if (Rig.Boom != null) Rig.Boom.Rotation = new Vector3(0, -(float)SailAngle, 0);
        if (Rig.Elevator != null) Rig.Elevator.Rotation = new Vector3((float)ElevatorIn * 0.35f, 0, 0);
        if (Rig.AileronL != null) Rig.AileronL.Rotation = new Vector3((float)AileronIn * 0.3f, 0, 0);
        if (Rig.AileronR != null) Rig.AileronR.Rotation = new Vector3(-(float)AileronIn * 0.3f, 0, 0);
        if (Rig.RudderNode != null) Rig.RudderNode.Rotation = new Vector3(0, -(float)RudderIn * 0.4f, 0);
        if (Rig.Motor != null) Rig.Motor.Rotation = new Vector3(0, (float)SteerAngle, 0);
        foreach (var l in Rig.BrakeLights) l.Visible = Braking;
        // pédalage (vélo) : jambes en opposition
        float a = (float)Math.Sin(Spin) * 0.55f;
        foreach (var l in Rig.LegsL) l.Rotation = new Vector3(0.5f + a, 0, 0);
        foreach (var l in Rig.LegsR) l.Rotation = new Vector3(0.5f - a, 0, 0);
    }

    CharacterModel _riderModel;

    /// <summary>Conducteur : le personnage du joueur (même apparence et tenue), assis sur le siège quand le véhicule
    /// est occupé ; caché en vue intérieure (la caméra est à sa place).</summary>
    void UpdateRider()
    {
        var seat = Rig.Rider;
        if (seat == null) return;
        seat.Visible = Occupied && !FirstPerson;
        if (!seat.Visible) return;
        if (_riderModel == null)
        {
            // créé à la première montée seulement (les véhicules garés n'ont pas de conducteur)
            _riderModel = new CharacterModel { Name = "Driver", IsPlayer = true };
            seat.AddChild(_riderModel);
        }
        var m = new CharacterMotion { Seat = Rig.Seat, Grounded = true, Pedal = Rig.Seat == CharacterSeat.Bike ? Spin : 0, Lean = Spec.TwoWheels ? Lean : 0 };
        _riderModel.Update(m, GetProcessDeltaTime());
        var pelvis = _riderModel.PelvisLocal; // le bassin repose sur le siège
        _riderModel.Position = new Vector3(-pelvis.X, 0.1f - pelvis.Y, -pelvis.Z + 0.05f);
    }

    readonly List<MeshInstance3D> _shell = new();
    bool _shellHidden;

    /// <summary>Vue intérieure : on cache les volumes qui entourent les yeux du conducteur (cabine vitrée, fuselage,
    /// carrosserie) ou qui sont droit devant eux, pour voir dehors ; le capot, les ailes et les roues restent visibles.</summary>
    void HideShell(bool hide)
    {
        _shellHidden = hide;
        foreach (var m in _shell) if (IsInstanceValid(m)) m.Visible = true;
        _shell.Clear();
        if (!hide || !IsInsideTree()) return;
        var eye = new Vector3(Spec.Eye.X, Spec.Eye.Y, -Spec.Eye.Z); // Godot : avant = −Z
        var inv = GlobalTransform.AffineInverse();
        var stack = new Stack<Node>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is not MeshInstance3D mi || !mi.Visible) continue;
            var local = inv * mi.GlobalTransform;
            var box = local * mi.GetAabb();
            // autour des yeux, ou droit devant (capot moteur et hélice d'avion qui boucheraient la vue)
            if (box.Grow(0.05f).HasPoint(eye) || box.IntersectsSegment(eye, eye + new Vector3(0, 0, -12)) != null) { mi.Visible = false; _shell.Add(mi); }
        }
    }

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

    /// <summary>Glisse le long des obstacles (murs, troncs, véhicules garés) : on teste plusieurs points le long du
    /// véhicule (un bus fait 12 m). Retourne vrai s'il y a eu contact.</summary>
    protected bool ResolveObstacles(double radius, double height)
    {
        double half = Math.Max(0, Spec.Length / 2 - radius);
        int n = Math.Max(1, (int)Math.Ceiling(half / Math.Max(radius, 0.5)));
        bool hit = false;
        for (int i = -n; i <= n; i++)
        {
            double o = half * i / n;
            var p = PosD + Fwd * o;
            var r = Obstacles.Resolve(p, radius, height);
            if ((r - p).Length() > 1e-4) { PosD = r - Fwd * o; hit = true; }
            if (n == 0) break;
        }
        return hit;
    }
}
