using System;

namespace SteamEarth;

/// <summary>
/// Bateaux (bateau à moteur, jet-ski, voilier) sur les lacs, rivières et la mer : physique arcade.
/// La vitesse tend vers « gaz × vitesse maximale » ; le gouvernail n'agit que s'il y a de l'eau qui le traverse
/// (vitesse ou souffle de l'hélice). Flottaison amortie vers la surface (<see cref="WaterSurface"/>, vagues comprises),
/// la coque prend la pente des vagues, se cabre en déjaugeant et gîte dans les virages. Elle s'échoue sur la rive.
/// Voilier : la poussée vient du vent réel (GFS) selon l'allure — rien face au vent (moins de 35°), le mieux au
/// travers / grand largue ; Z / S hissent ou affalent les voiles, Maj lance le petit moteur auxiliaire.
/// </summary>
public partial class Boat : Vehicle
{
    public double V;
    double _vSide, _vUp, _rudder, _yawRate, _sails, _sailForce;
    Vec3d _heading = new(0, 0, -1), _wind = new(0, 0, 0);
    bool _afloat = true, _beached, _motorOn;

    public Boat(VehicleSpec spec) : base(spec) { }

    bool Sail => Spec.Family == VehicleFamily.Sailboat;
    public override double Speed => V;
    public override bool CanExit => Math.Abs(V) < 2;
    public override double Radius => Spec.Width / 2 + 0.2;
    public override double BodyHeight => 2.0;
    public override Vec3d ExitPoint => PosD - Right * (Spec.Width / 2 + 0.6);
    /// <summary>Voilier : voiles hissées 0..1.</summary>
    public double Sails => _sails;
    /// <summary>Vent apparent pour le tableau de bord : direction relative à l'avant (rad, + = de tribord) et force (m/s).</summary>
    public (double angle, double speed) ApparentWind
    {
        get
        {
            var up = PosD.Normalized();
            var aw = _wind - Tangent(_heading, up) * V;
            var f = Tangent(_heading, up); var r = f.Cross(up).Normalized();
            return (Math.Atan2(-aw.Dot(r), -aw.Dot(f)), (aw - up * aw.Dot(up)).Length());
        }
    }
    public bool Beached => _beached;
    public bool Afloat => _afloat;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        _heading = Fwd;
        V = 0; _vSide = 0; _vUp = 0; Throttle01 = 0; _sails = 0;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(V) * dt / 0.5), 1, 16);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
        // régime : suit les gaz, monte un peu avec la charge
        double target = Sail ? (_motorOn ? 0.35 : 0) : 0.12 + 0.85 * Math.Abs(Throttle01);
        Rpm01 += (target - Rpm01) * Math.Min(1, dt * 3);
    }

    static double Level(Vec3d p, double fallback) => WaterSurface.At(p, out double l) ? l : fallback;

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        var head = Tangent(_heading, up);
        var right = head.Cross(up).Normalized();
        double len = Spec.Length, beam = Spec.Width;
        _wind = Wind(PosD, 10);
        _wind -= up * _wind.Dot(up);

        double thrust; // accélération voulue (m/s²)
        if (Sail)
        {
            // Z hisse, S affale (et un peu de marche arrière au moteur)
            if (input.Throttle > 0) _sails = Math.Min(1, _sails + 0.4 * h);
            if (input.Throttle < 0) _sails = Math.Max(0, _sails - 0.6 * h);
            _motorOn = input.Boost || (input.Throttle < 0 && _sails <= 0);
            // allure : angle entre l'avant et la provenance du vent (0 = face au vent)
            double ws = _wind.Length();
            double from = ws > 0.1 ? Math.Acos(Math.Clamp(-_wind.Dot(head) / ws, -1, 1)) : 0;
            double polar = from < 0.6 ? 0 : from < 0.9 ? (from - 0.6) / 0.3 * 0.75 : from < 2.4 ? 0.75 + 0.25 * Math.Sin((from - 0.9) / 1.5 * Math.PI) : 0.75 - 0.15 * (from - 2.4) / 0.74;
            double target = Math.Min(Spec.TopSpeed, ws * 0.55 * polar + 0.4 * polar) * _sails;
            _sailForce = polar * _sails;
            thrust = (target - V) * 0.25;
            if (_motorOn) thrust += input.Throttle < 0 ? -1.0 : (3.5 - V) > 0 ? 0.6 : 0;
            // bôme sous le vent : choquée au portant, bordée au près
            var windSide = _wind.Dot(right); // vent qui vient de bâbord pousse vers tribord
            double boom = Math.Clamp(Math.PI - from, 0.15, 1.35) * 0.9 * (windSide >= 0 ? 1 : -1);
            SailAngle += (boom * _sails - SailAngle) * Math.Min(1, 1.5 * h);
            Throttle01 = _motorOn ? (input.Throttle < 0 ? -0.6 : 0.6) : 0;
        }
        else
        {
            double target = input.Throttle > 0 ? input.Throttle : input.Throttle * 0.45;
            Throttle01 += Math.Clamp(target - Throttle01, -0.9 * h, 0.9 * h);
            double vTarget = Throttle01 >= 0 ? Throttle01 * Spec.TopSpeed : Throttle01 / 0.45 * Spec.Reverse;
            double rate = (vTarget - V) * (Spec.Accel / Math.Max(Spec.TopSpeed * 0.35, 1));
            thrust = Math.Clamp(rate, -Spec.Accel * (V > vTarget && Throttle01 < 0 ? 1.4 : 0.8), Spec.Accel);
        }
        Braking = false;
        _rudder += Math.Clamp(input.Steer - _rudder, -3 * h, 3 * h);
        SteerAngle = _rudder * 0.5;

        _afloat = WaterSurface.At(PosD, out double level);
        if (_afloat)
        {
            V += thrust * h;
            // Gouvernail : efficace avec la vitesse ou le souffle de l'hélice
            double flow = V + Math.Abs(Throttle01) * 4;
            _yawRate = _rudder * Spec.TurnRate * Math.Clamp(flow / 4, -1, 1);
            double maxYaw = 0.55 * G / Math.Max(Math.Abs(V), 1);
            _yawRate = Math.Clamp(_yawRate, -maxYaw, maxYaw);
            head = Rotate(head, up, -_yawRate * h);
            // Dérive : la coque glisse dans les virages et sous le vent (davantage le voilier)
            double windSide = _wind.Dot(right);
            _vSide += (V * _yawRate * 0.06 + windSide * (Sail ? 0.05 * _sails + 0.01 : 0.02) - _vSide * 1.5) * h;
            Slip = Math.Clamp(Math.Abs(V * _yawRate) / 6, 0, 1) * (Sail ? 0 : 1); // gerbe d'eau
        }
        else
        {
            V *= Math.Max(0, 1 - 8 * h);
            _vSide = 0; Slip = 0;
        }

        // Avance, sauf si la proue touche la rive
        var next = PosD + head * (V * h) + right * (_vSide * h);
        var bow = next + head * (Math.Sign(V == 0 ? 1 : V) * len / 2);
        _beached = false;
        if (!WaterSurface.At(bow, out _)) // la proue touche la rive (même plate) : on n'avance plus
        {
            if (Math.Abs(V) > 3) { Notice = Loc.T("Échoué sur la rive !"); Impact = Math.Clamp(Math.Abs(V) / 12, 0.3, 1); }
            V *= -0.15; _vSide = 0; _beached = true;
        }
        else
        {
            PosD = next;
            if (ResolveObstacles(Radius, BodyHeight)) { if (Math.Abs(V) > 4) { Notice = Loc.T("Choc !"); Impact = Math.Clamp(Math.Abs(V) / 12, 0.3, 1); } V *= Math.Max(0, 1 - 6 * h); }
        }

        // Flottaison : ressort amorti vers la surface (vagues comprises)
        up = PosD.Normalized();
        double alt = PosD.Length() - Geo.Radius;
        if (WaterSurface.At(PosD, out level))
        {
            double hull = level - Spec.Draft * 0.3;
            _vUp += (14 * (hull - alt) - 4 * _vUp) * h;
            PosD += up * (_vUp * h);
        }
        else
        {
            double ground = Ground(PosD, out _);
            PosD = up * (Geo.Radius + Math.Max(ground, alt - 0.5 * h * G));
            if (PosD.Length() - Geo.Radius <= ground + 0.01) _vUp = 0;
        }

        // Assiette : pente des vagues + cabrage en déjaugeant + gîte (virage, et vent dans les voiles)
        double lc = PosD.Length() - Geo.Radius;
        double lf = Level(PosD + head * (len / 2), lc), lb = Level(PosD - head * (len / 2), lc);
        double lr = Level(PosD + right * (beam / 2), lc), ll = Level(PosD - right * (beam / 2), lc);
        double s = Math.Clamp(Math.Abs(V) / Math.Max(Spec.TopSpeed * 0.75, 1), 0, 1);
        double trim = Sail ? 0 : 0.08 * Math.Sin(s * Math.PI) + 0.03 * s;
        double heel = Math.Clamp(V * _yawRate * (Spec.Id == "jetski" ? -0.06 : 0.03), -0.4, 0.4);
        if (Sail) heel += Math.Clamp(-_wind.Dot(right) * 0.025 * _sails * _sailForce, -0.35, 0.35);
        Lean = Spec.Id == "jetski" ? -heel : 0;
        var normal = (up - head * ((lf - lb) / len + trim) - right * ((lr - ll) / beam - heel)).Normalized();
        double k = Math.Min(1, 4 * h);
        Up = (Up * (1 - k) + normal * k).Normalized();
        _heading = head;
        Fwd = Tangent(head, Up);
        Spin += (Sail ? (_motorOn ? 0.5 : 0) : Math.Abs(Throttle01) * 0.9 + 0.1) * 60 * h;
    }

    public override string Hud()
    {
        string s = $"{Loc.T(Spec.Name)} : {Math.Abs(V) * 3.6:N0} km/h ({Math.Abs(V) * 1.944:N1} {Loc.T("nœuds")})";
        if (Sail)
        {
            var (a, ws) = ApparentWind;
            s += $"   {Loc.T("voiles")} {_sails * 100:N0} %   {Loc.T("vent apparent")} {ws * 1.944:N0} {Loc.T("nœuds")} {Math.Abs(a) * 180 / Math.PI:N0}° {Loc.T(a >= 0 ? "tribord" : "bâbord")}";
            if (_sails > 0.3 && _sailForce < 0.05) s += "   " + Loc.T("face au vent : les voiles faseyent");
            if (_motorOn) s += "   " + Loc.T("moteur");
        }
        else s += $"   {Loc.T("moteur")} {Throttle01 * 100:+0;-0;0} %";
        if (_beached) s += "   " + Loc.T("échoué");
        else if (!_afloat) s += "   " + Loc.T("à terre");
        string keys = Sail ? "Z hisser, S affaler, Q/D barre, Maj moteur, V vue, E descendre" : "Z avant, S arrière, Q/D gouvernail, V vue, E descendre";
        return s + "   [" + Loc.T(keys) + "]";
    }
}
