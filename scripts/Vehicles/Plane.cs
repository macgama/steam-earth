using System;

namespace SteamEarth;

/// <summary>
/// Avions (tourisme, ligne) : vol arcade, facile au clavier.
/// S cabre / Z pique (taux de tangage), Q/D inclinent (l'avion revient à plat quand on lâche), ←/→ palonnier ;
/// le virage est coordonné (lacet = g·tan(inclinaison)/v). Maj/Ctrl règlent les gaz. La vitesse vient des gaz
/// et de la pente (on accélère en piqué, on ralentit en montée) ; sous la vitesse de décrochage, le nez tombe.
/// La vitesse sol est la vitesse air plus le vent réel (GFS) : par vent de travers, l'avion dérive.
/// Au sol : roulage, freins, roue avant directrice ; rotation possible au-delà de la vitesse de décollage.
/// Toucher trop fort, trop incliné, sur l'eau ou contre un bâtiment = crash.
/// </summary>
public partial class Plane : Vehicle
{
    /// <summary>Vitesse air le long de l'axe (m/s).</summary>
    public double V;
    public Vec3d Vel;
    public double Throttle;
    public bool OnGround = true;
    double _vUp, _bank, _pitchRate;
    Vec3d _wind = new(0, 0, 0);

    public Plane(VehicleSpec spec) : base(spec) { }

    bool Jet => Spec.Family == VehicleFamily.Jet;
    public override double Speed => V;
    public override bool CanExit => OnGround && V < 2;
    public override double Radius => Jet ? 6 : 5.5;
    public override double BodyHeight => Spec.Height;
    public override (double halfLength, double halfWidth, double height) Footprint => Jet ? (0, 0, 0) : (Spec.Length / 2, 0.7, 2.1); // sous l'aile haute, on passe
    public override Vec3d ExitPoint => PosD - Right * (Jet ? 4 : 2.2) + Fwd * 0.5;
    public bool Stalling => !OnGround && V < Spec.StallSpeed;
    /// <summary>Vitesse de rotation (décollage).</summary>
    public double RotateSpeed => Spec.StallSpeed * 1.1;
    public double VerticalSpeed => Vel.Dot(PosD.Normalized());
    public Vec3d WindNow => _wind;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        V = 0; Vel = new Vec3d(0, 0, 0); _vUp = 0;
        Throttle = 0; OnGround = true; Grounded = true;
    }

    /// <summary>Met l'avion en vol en <paramref name="pos"/>, à <paramref name="speed"/> m/s, en palier.</summary>
    public void PlaceInAir(Vec3d pos, Vec3d heading, double speed)
    {
        var up = pos.Normalized();
        PosD = pos; Up = up; Fwd = Tangent(heading, up);
        V = Math.Max(speed, Spec.StallSpeed * 1.5);
        Vel = Fwd * V;
        Throttle = 0.65; OnGround = false; Grounded = false;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(dt * 60), 1, 8);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
        Throttle01 = Throttle;
        double target = 0.15 + 0.85 * Throttle;
        Rpm01 += (target - Rpm01) * Math.Min(1, dt * (Jet ? 0.8 : 2.5)); // un réacteur monte lentement en régime
    }

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        if (input.ThrottleUp) Throttle = Math.Min(1, Throttle + 0.45 * h);
        if (input.ThrottleDown) Throttle = Math.Max(0, Throttle - 0.45 * h);
        double ground = Ground(PosD, out _);
        double alt = PosD.Length() - Geo.Radius;
        _wind = Wind(PosD, Math.Max(2, alt - ground));

        double stall = Spec.StallSpeed, top = Spec.TopSpeed;
        double eff = Math.Clamp((V - stall * 0.5) / stall, 0, 1); // efficacité des gouvernes

        // --- Vitesse : gaz (poussée qui diminue vers la vitesse maximale), pente, traînée au ralenti, freins au sol
        double sinPitch = Math.Clamp(Fwd.Dot(up), -1, 1);
        double a = Throttle * Spec.Accel * Math.Max(0, 1 - (V / top) * (V / top));
        a -= G * sinPitch * (OnGround ? 0 : 0.85);
        a -= (1 - Throttle) * 0.12 * Spec.Accel * (V / top) * (V / top) * 3 + (OnGround ? 0.3 : 0);
        Braking = OnGround && input.Brake;
        if (Braking) a -= Spec.Brake;
        V = Math.Max(0, V + a * h);

        // --- Commandes
        double pitchIn = -input.Throttle; // S = tirer (cabrer), Z = pousser
        ElevatorIn += (pitchIn - ElevatorIn) * Math.Min(1, 8 * h);
        AileronIn += ((OnGround ? 0 : input.Steer) - AileronIn) * Math.Min(1, 8 * h);
        RudderIn += (input.Rudder + (OnGround ? input.Steer : 0) - RudderIn) * Math.Min(1, 8 * h);

        var f = Fwd; var u = Up; var right = Right;
        if (OnGround)
        {
            // Roulage : roue avant directrice ; rotation (cabrer) au-delà de la vitesse de décollage
            double steer = (input.Steer + input.Rudder) * 0.5 * Math.Clamp(V / 2.5, 0, 1) / (1 + V / 15);
            f = Rotate(f, up, -steer * h);
            SteerAngle = -(input.Steer + input.Rudder) * 0.35;
            double pitchNow = Math.Asin(Math.Clamp(f.Dot(up), -1, 1));
            double pitchTarget = V >= RotateSpeed && pitchIn > 0 ? 0.2 * pitchIn : 0;
            double dp = Math.Clamp(pitchTarget - pitchNow, -0.5 * h, 0.5 * h);
            f = Rotate(f, f.Cross(up).Normalized(), dp);
            _bank = 0;
            // décollage : au-delà de la vitesse de décollage, l'aile porte selon l'assiette
            double lift = V > RotateSpeed * 0.95 ? V * Math.Sin(Math.Max(0, pitchNow)) : 0;
            _vUp = Math.Max(0, lift);
        }
        else
        {
            SteerAngle = 0;
            // Roulis : Q/D donnent un taux, l'avion revient à plat seul ; inclinaison limitée à 60°
            double bankTarget = input.Steer * (Jet ? 0.55 : 1.0);
            double rollRate = Spec.RollRate * eff;
            _bank += Math.Clamp(bankTarget - _bank, -rollRate * h, rollRate * h) * (input.Steer == 0 ? 0.8 : 1);
            // Tangage : taux voulu, la profondeur est moins efficace à basse vitesse ; décrochage : le nez tombe
            double target = pitchIn * Spec.PitchRate * eff;
            _pitchRate += (target - _pitchRate) * Math.Min(1, 4 * h);
            double pr = _pitchRate * Math.Cos(_bank);
            if (V < stall) pr -= (stall - V) / stall * 0.8; // le nez tombe, l'avion reprend de la vitesse
            // assiette limitée : pas de looping au clavier
            double pitchNow = Math.Asin(Math.Clamp(f.Dot(up), -1, 1));
            if (pitchNow > 0.6 && pr > 0) pr *= Math.Max(0, (0.8 - pitchNow) / 0.2);
            if (pitchNow < -0.7 && pr < 0) pr *= Math.Max(0, (pitchNow + 0.9) / 0.2);
            var horiz = f.Cross(up).Normalized();
            f = Rotate(f, horiz, pr * h);
            // Virage coordonné + palonnier
            double yaw = G * Math.Tan(_bank) / Math.Max(V, stall * 0.6) + input.Rudder * 0.12 * eff;
            f = Rotate(f, up, -yaw * h);
        }
        f = f.Normalized();
        // Haut : la verticale locale inclinée de l'angle de roulis autour de l'avant
        var horizRight = f.Cross(up).Normalized();
        var level = horizRight.Cross(f).Normalized();
        u = Rotate(level, f, _bank);
        Fwd = f; Up = (u - f * u.Dot(f)).Normalized();

        // --- Mouvement : vitesse air le long de l'axe + vent
        Vel = OnGround ? Tangent(Fwd, up) * V + up * _vUp : Fwd * V + _wind;
        PosD += Vel * h;

        // --- Contact avec le sol (le point de référence est sous les roues)
        up = PosD.Normalized();
        ground = Ground(PosD, out bool water);
        double above = PosD.Length() - Geo.Radius - ground;
        if (above <= 0)
        {
            double vn = Vel.Dot(up);
            if (!OnGround)
            {
                double bank = Math.Abs(BankAngle);
                if (water) { Crash(string.Format(Loc.T("Amerrissage à {0:N0} km/h : cet avion n'a pas de flotteurs !"), V * 3.6)); return; }
                if (vn < -6 || vn > 3 || V > Spec.StallSpeed * 1.7 || bank > 0.5 || PitchAngle < -0.2) { Crash(string.Format(Loc.T("Crash ! (vitesse verticale {0:F1} m/s, inclinaison {1:F0}°)"), vn, bank * 180 / Math.PI)); return; }
                Notice = string.Format(Loc.T("Posé ! (vitesse verticale {0:F1} m/s)"), vn);
                Impact = Math.Clamp(-vn / 8, 0.15, 0.6);
            }
            PosD = up * (Geo.Radius + ground);
            _vUp = 0;
            OnGround = true;
        }
        else if (above > 0.3 && _vUp > 0 || above > 3) OnGround = false;
        Grounded = OnGround;

        if (OnGround) AlignOnGround(up);
        else
        {
            var resolved = Obstacles.Resolve(PosD, Radius * 0.7, 3);
            if ((resolved - PosD).Length() > 1e-3) { Crash(Loc.T("Collision avec un obstacle !")); return; }
        }
        if (OnGround)
        {
            if (ResolveObstacles(1.5, BodyHeight) && V > 4) { Notice = Loc.T("Choc !"); Impact = 0.6; V *= 0.3; }
            WheelAngle += V * h / 0.3;
        }
        Spin += (Throttle * 0.85 + 0.15) * (Jet ? 40 : 160) * h;
    }

    /// <summary>Au sol : pas d'inclinaison latérale, l'assiette suit la piste (cabrage limité pour la rotation).</summary>
    void AlignOnGround(Vec3d up)
    {
        var ft = Tangent(Fwd, up);
        var rt = ft.Cross(up).Normalized();
        double l = Math.Min(Spec.Length * 0.35, 6);
        double hf = Ground(PosD + ft * l, out _), hb = Ground(PosD - ft * (l * 0.4), out _);
        double hr = Ground(PosD + rt * 1.3, out _), hl = Ground(PosD - rt * 1.3, out _);
        var n = (up - ft * ((hf - hb) / (l * 1.4)) - rt * ((hr - hl) / 2.6)).Normalized();
        var fn = Tangent(Fwd, n);
        double p = Math.Clamp(Math.Asin(Math.Clamp(Fwd.Dot(n), -1, 1)), 0, 0.22);
        Fwd = (fn * Math.Cos(p) + n * Math.Sin(p)).Normalized();
        var r = Fwd.Cross(n).Normalized();
        Up = r.Cross(Fwd).Normalized();
    }

    void Crash(string message)
    {
        Notice = message;
        Impact = 1;
        var up = PosD.Normalized();
        PosD = up * (Geo.Radius + Ground(PosD, out _));
        Vel = new Vec3d(0, 0, 0);
        V = 0; Throttle = 0; _bank = 0; _vUp = 0;
        OnGround = true; Grounded = true;
        Up = up;
        Fwd = Tangent(Fwd, up);
    }

    public override string Hud()
    {
        var up = PosD.Normalized();
        double alt = PosD.Length() - Geo.Radius;
        double gs = (Vel - up * Vel.Dot(up)).Length();
        Geo.LocalFrame(up, out var east, out var north);
        double we = _wind.Dot(east), wn = _wind.Dot(north), ws = Math.Sqrt(we * we + wn * wn);
        double from = (Math.Atan2(-we, -wn) * 180 / Math.PI + 360) % 360;
        string s = $"{Loc.T(Spec.Name)} : {Loc.T("vitesse air")} {V * 3.6:N0} km/h, {Loc.T("sol")} {gs * 3.6:N0} km/h   {Loc.T("altitude")} {alt:N0} m ({Loc.T("hauteur")} {HeightAboveGround:N0} m)   " +
                   $"{Loc.T("vario")} {VerticalSpeed:+0.0;-0.0;0.0} m/s   {Loc.T("gaz")} {Throttle * 100:N0} %   {Loc.T("vent réel")} {ws * 3.6:N0} km/h {Loc.T("du")} {from:F0}°";
        if (Stalling) s += "   ⚠ " + Loc.T("DÉCROCHAGE");
        if (OnGround) s += "   " + Loc.T(V < 2 ? "(au sol)" : V >= RotateSpeed ? "(tire S pour décoller)" : "(roulage)");
        return s + "   [" + Loc.T("Maj/Ctrl gaz, S cabrer, Z piquer, Q/D incliner (au sol : diriger), ←/→ palonnier, Espace freins, V vue, E descendre") + "]";
    }
}
