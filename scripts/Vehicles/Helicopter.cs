using System;

namespace SteamEarth;

/// <summary>
/// Hélicoptère : vol arcade, facile au clavier. Maj monte, Ctrl descend, Z/S avancent / reculent, Q/D tournent,
/// ←/→ glissent de côté. La vitesse tend vers la consigne (comme un pilote automatique de stabilisation),
/// l'appareil s'incline visuellement dans le sens du mouvement et dérive avec le vent réel.
/// Le rotor doit monter en régime avant de décoller ; se poser trop vite ou contre un bâtiment = crash.
/// </summary>
public partial class Helicopter : Vehicle
{
    /// <summary>Vitesse horizontale (repère planète, tangente) et verticale (m/s).</summary>
    public Vec3d Vel = new(0, 0, 0);
    double _vUp, _rotor, _yaw, _tiltF, _tiltR;
    Vec3d _heading = new(0, 0, -1), _wind = new(0, 0, 0);

    public Helicopter(VehicleSpec spec) : base(spec) { }

    public override double Speed => (Vel - PosD.Normalized() * Vel.Dot(PosD.Normalized())).Length();
    public override bool CanExit => Grounded && Speed < 1.5;
    public override double Radius => 1.6;
    public override (double halfLength, double halfWidth, double height) Footprint => (Spec.Length * 0.35, 1.2, 2.6);
    public double VerticalSpeed => _vUp;
    /// <summary>Régime du rotor 0..1 (il faut ~1 pour décoller).</summary>
    public double Rotor => _rotor;
    public Vec3d WindNow => _wind;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        _heading = Fwd; Vel = new Vec3d(0, 0, 0); _vUp = 0; Grounded = true;
    }

    /// <summary>En vol stationnaire en <paramref name="pos"/> (rotor lancé).</summary>
    public void PlaceInAir(Vec3d pos, Vec3d heading)
    {
        var up = pos.Normalized();
        PosD = pos; Up = up; _heading = Fwd = Tangent(heading, up);
        Vel = new Vec3d(0, 0, 0); _vUp = 0; _rotor = 1; Grounded = false;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(dt * 60), 1, 8);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
        Rpm01 = 0.15 + 0.85 * _rotor * (0.85 + 0.15 * Throttle01);
    }

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        var head = Tangent(_heading, up);
        var right = head.Cross(up).Normalized();
        double alt = PosD.Length() - Geo.Radius;
        double ground = Ground(PosD, out bool water);
        _wind = Wind(PosD, Math.Max(2, alt - ground));
        _wind -= up * _wind.Dot(up);

        // Le rotor se lance dès qu'on est à bord, et ralentit posé sans commande
        bool wantsLift = input.ThrottleUp || !Grounded || input.Throttle != 0;
        _rotor = Math.Clamp(_rotor + (Occupied && (wantsLift || _rotor > 0.2) ? 0.25 : -0.15) * h, 0, 1);
        double power = _rotor >= 0.95 ? 1 : 0;

        // Consignes
        double fwd = input.Throttle, side = input.Rudder;
        double climb = (input.ThrottleUp ? 1 : 0) - (input.ThrottleDown ? 1 : 0);
        Throttle01 = Math.Clamp(0.55 + 0.45 * climb + 0.25 * Math.Abs(fwd), 0, 1);
        double top = Spec.TopSpeed;
        var target = (head * (fwd >= 0 ? fwd * top : fwd * top * 0.3) + right * (side * 12)) * power;

        // Vitesse horizontale par rapport à l'air (le vent fait dériver)
        var air = Vel - _wind;
        var dv = target - air;
        double maxA = Spec.Accel * (Grounded ? 0.3 : 1);
        if (dv.Length() > maxA * h) dv = dv.Normalized() * (maxA * h);
        if (!Grounded) Vel = air + dv + _wind;
        else Vel = (Vel + dv) * Math.Max(0, 1 - 3 * h);

        // Verticale
        double vTarget = power * climb * Spec.ClimbRate - (1 - power) * (Grounded ? 0 : 6);
        _vUp += Math.Clamp(vTarget - _vUp, -4 * h, 4 * h);

        // Lacet (Q/D), plus doux en vitesse
        double yawRate = input.Steer * Spec.TurnRate * (Grounded ? (power > 0 ? 0.4 : 0) : 1) / (1 + Speed / 40);
        _yaw = yawRate;
        head = Rotate(head, up, -yawRate * h);

        // Déplacement
        var hv = Vel - up * Vel.Dot(up);
        PosD += (hv + up * _vUp) * h;

        // Sol, eau, bâtiments
        up = PosD.Normalized();
        ground = Ground(PosD, out water);
        double above = PosD.Length() - Geo.Radius - ground;
        if (above <= 0)
        {
            if (!Grounded)
            {
                if (water) { Crash(Loc.T("L'hélicoptère est tombé à l'eau !")); return; }
                if (_vUp < -5 || hv.Length() > 12) { Crash(string.Format(Loc.T("Crash ! (vitesse verticale {0:F1} m/s)"), _vUp)); return; }
                Notice = string.Format(Loc.T("Posé ! (vitesse verticale {0:F1} m/s)"), _vUp);
                Impact = Math.Clamp(-_vUp / 8, 0.1, 0.5);
            }
            PosD = up * (Geo.Radius + ground);
            if (_vUp < 0) _vUp = 0;
            Grounded = true;
        }
        else if (above > 0.2) Grounded = false;
        if (!Grounded)
        {
            var resolved = Obstacles.Resolve(PosD, 3, 3.4);
            if ((resolved - PosD).Length() > 1e-3) { Crash(Loc.T("Collision avec un obstacle !")); return; }
        }
        else if (ResolveObstacles(Radius, BodyHeight)) Vel = new Vec3d(0, 0, 0);

        // Inclinaison visuelle : nez vers le bas en accélérant vers l'avant, penché du côté où l'on glisse
        var local = Vel - _wind;
        double vf = local.Dot(head), vr = local.Dot(right);
        double kt = Math.Min(1, 2.5 * h);
        double tf = Grounded ? 0 : Math.Clamp(-vf / top * 0.28 - (target.Dot(head) - vf) * 0.02, -0.35, 0.35);
        double tr = Grounded ? 0 : Math.Clamp(vr / 12 * 0.2 + yawRate * Speed * 0.01, -0.35, 0.35);
        _tiltF += (tf - _tiltF) * kt; _tiltR += (tr - _tiltR) * kt;
        _heading = head;
        var f = Rotate(head, head.Cross(up).Normalized(), _tiltF);
        var u = Rotate(up, head.Cross(up).Normalized(), _tiltF);
        u = Rotate(u, f, _tiltR);
        Fwd = f.Normalized(); Up = (u - Fwd * u.Dot(Fwd)).Normalized();
        ElevatorIn = 0; RudderIn = input.Steer;
        Spin += _rotor * 28 * h;
    }

    void Crash(string message)
    {
        Notice = message;
        Impact = 1;
        var up = PosD.Normalized();
        PosD = up * (Geo.Radius + Ground(PosD, out _));
        Vel = new Vec3d(0, 0, 0); _vUp = 0; _rotor = 0;
        Grounded = true;
        Up = up; Fwd = Tangent(_heading, up);
    }

    public override string Hud()
    {
        double alt = PosD.Length() - Geo.Radius;
        string s = $"{Loc.T(Spec.Name)} : {Speed * 3.6:N0} km/h   {Loc.T("altitude")} {alt:N0} m ({Loc.T("hauteur")} {HeightAboveGround:N0} m)   {Loc.T("vario")} {_vUp:+0.0;-0.0;0.0} m/s   {Loc.T("rotor")} {_rotor * 100:N0} %";
        if (_rotor < 0.95 && Occupied) s += "   " + Loc.T("(le rotor se lance…)");
        return s + "   [" + Loc.T("Maj monter, Ctrl descendre, Z/S avancer / reculer, Q/D tourner, ←/→ glisser, V vue, E descendre") + "]";
    }
}
