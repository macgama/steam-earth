using System;

namespace SteamEarth;

/// <summary>
/// Véhicules à roues (voitures, utilitaires, bus, camion, tracteur, vélo, moto) : physique arcade, facile au clavier.
/// Accélération qui diminue vers la vitesse maximale, pente ressentie en partie, freinage puis marche arrière avec S,
/// direction moins sensible à haute vitesse. L'adhérence limite le virage : au-delà, l'arrière glisse un peu
/// (angle de dérive entre l'avant du véhicule et sa trajectoire), et le frein à main (Espace) provoque une glissade
/// contrôlée. Boîte automatique (régime et rapport pour le son et le tableau de bord). Les deux-roues se penchent
/// dans les virages et ne dérapent pas. Le véhicule quitte le sol sur une bosse si le relief descend plus vite que la gravité.
/// </summary>
public partial class RoadVehicle : Vehicle
{
    /// <summary>Vitesse le long de la trajectoire (m/s, négative en marche arrière).</summary>
    public double V;
    Vec3d _heading = new(0, 0, -1), _move = new(0, 0, -1);
    double _vUp, _slope, _drift, _rpm, _shiftTimer;
    bool _inWater;

    public RoadVehicle(VehicleSpec spec) : base(spec) { }

    bool TwoWheels => Spec.TwoWheels;
    bool Bike => Spec.Family == VehicleFamily.Bicycle;
    public override double Speed => V;
    public override bool CanExit => Math.Abs(V) < (TwoWheels ? 3 : 2.5);
    public override double Radius => TwoWheels ? 0.4 : Spec.Width / 2;
    /// <summary>Dérive (rad) : angle entre l'avant et la trajectoire, + = l'arrière part vers la gauche.</summary>
    public double DriftAngle => _drift;
    public double SlopePercent => _slope * 100;
    public bool InWater => _inWater;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        _heading = _move = Fwd;
        V = 0; _vUp = 0; SteerAngle = 0; _drift = 0; Grounded = true;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(V) * dt / 0.5), 1, 24);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
        Gearbox(input, dt);
    }

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        var head = Tangent(_heading, up);
        var move = Tangent(_move, up);
        var right = head.Cross(up).Normalized();
        double wb = Spec.Wheelbase;

        // Relief sous les roues : pente avant/arrière et gauche/droite
        Ground(PosD, out bool water);
        double hf = Ground(PosD + head * (wb / 2), out _), hb = Ground(PosD - head * (wb / 2), out _);
        double slopeF = (hf - hb) / wb, slopeR = 0;
        if (!TwoWheels)
        {
            double hr = Ground(PosD + right * (Spec.Track / 2), out _), hl = Ground(PosD - right * (Spec.Track / 2), out _);
            slopeR = (hr - hl) / Spec.Track;
        }
        _slope = slopeF;
        _inWater = water;
        Braking = false;
        Throttle01 = Math.Max(0, input.Throttle);

        if (Grounded)
        {
            double thr = input.Throttle;
            double top = Spec.TopSpeed, accel = Spec.Accel;
            if (Bike && input.Boost) { top *= 1.35; accel *= 1.7; } // en danseuse
            if (_inWater) { thr *= 0.15; top *= 0.15; }             // les roues patinent, le moteur se noie
            double a = 0, resist = 0;
            if (thr > 0)
            {
                if (V >= -0.3) a = thr * accel * Math.Max(0, 1 - (V / top) * (V / top));
                else { resist += Spec.Brake * thr; Braking = true; } // on freine la marche arrière avant de repartir
            }
            else if (thr < 0)
            {
                if (V > 0.3) { resist += Spec.Brake * -thr; Braking = true; }
                else a = thr * accel * 0.7 * Math.Max(0, 1 - (V / Spec.Reverse) * (V / Spec.Reverse));
            }
            if (input.Brake) { resist += Spec.Brake * (TwoWheels ? 0.7 : 0.45); Braking = true; }

            // Pente (en partie : arcade), roulement et frein moteur au lâcher, traînée vers la vitesse maximale
            double sinSlope = slopeF / Math.Sqrt(1 + slopeF * slopeF);
            a -= G * sinSlope * Spec.SlopeFactor;
            if (thr == 0) resist += 0.25 + 0.35 * accel * (V / top) * (V / top);
            if (_inWater) resist += 3;
            double windAlong = Wind(PosD, 2).Dot(head);
            if (Bike) a -= 0.004 * (V - windAlong) * Math.Abs(V - windAlong); // le vent se sent à vélo

            V += a * h;
            double dv = resist * h; // le roulement et les freins s'opposent au mouvement sans jamais l'inverser
            V = Math.Abs(V) <= dv ? 0 : V - Math.Sign(V) * dv;

            // Direction : moins de braquage à haute vitesse (aide au clavier), retour au centre rapide
            double fade = TwoWheels ? 7 : 11;
            double maxSteer = Spec.MaxSteer / (1 + (V / fade) * (V / fade));
            double target = input.Steer * maxSteer;
            double rate = input.Steer == 0 ? 4 : 2.2;
            SteerAngle += Math.Clamp(target - SteerAngle, -rate * h, rate * h);

            // Lacet de l'avant ; la trajectoire suit dans la limite de l'adhérence
            double speed = Math.Abs(V);
            double yaw = V * Math.Tan(SteerAngle) / wb;
            double maxYaw = Spec.Grip * G / Math.Max(speed, 0.5);
            bool handbrake = input.Brake && !TwoWheels && speed > 6;
            if (TwoWheels) yaw = Math.Clamp(yaw, -maxYaw, maxYaw);
            else yaw = Math.Clamp(yaw * (handbrake ? 1.6 : 1), -maxYaw * (handbrake ? 2.2 : 1.25), maxYaw * (handbrake ? 2.2 : 1.25));
            head = Rotate(head, up, -yaw * h);

            // La trajectoire rejoint l'avant (adhérence), moins vite au frein à main : glissade
            double ang = SignedAngle(move, head, up);
            double follow = TwoWheels || speed < 3 ? 1e9 : maxYaw * (handbrake ? 0.45 : 1.1);
            double turn = Math.Clamp(ang, -follow * h, follow * h);
            if (speed < 3) turn = ang;
            move = Rotate(move, up, turn);
            ang = SignedAngle(move, head, up);
            // l'avant se réaligne sur la trajectoire (contre-braquage automatique : facile au clavier)
            double align = handbrake ? 0.6 : 2.5;
            if (!TwoWheels && Math.Abs(ang) > 0.01) head = Rotate(head, up, -ang * Math.Min(1, align * h));
            _drift = SignedAngle(move, head, up);
            V -= Math.Sign(V) * Math.Min(Math.Abs(V), Math.Abs(Math.Sin(_drift)) * G * 0.5 * h); // les pneus frottent
            Slip = Math.Clamp(Math.Abs(_drift) * 5 - 0.1, 0, 1) * Math.Clamp(speed / 6, 0, 1);
            if (Braking && speed > 12 && (input.Throttle < -0.5 || input.Brake) && !Bike) Slip = Math.Max(Slip, 0.35);
            if (Bike) Slip = 0;

            if (TwoWheels)
            {
                double leanTarget = Math.Clamp(Math.Atan(V * yaw / G), -0.75, 0.75);
                Lean += (leanTarget - Lean) * Math.Min(1, 6 * h);
            }
        }
        else Slip = 0;

        // Avance le long de la trajectoire, puis glissement le long des obstacles
        _heading = head; _move = move;
        PosD += (move * V) * h;
        if (ResolveObstacles(Radius, BodyHeight))
        {
            if (Math.Abs(V) > 4) { Notice = Loc.T("Choc !"); Impact = Math.Max(Impact, Math.Clamp(Math.Abs(V) / 20, 0.2, 1)); }
            V *= Math.Max(0, 1 - 6 * h);
        }

        // Vertical : gravité, contact avec le sol
        _vUp -= G * h;
        PosD += PosD.Normalized() * (_vUp * h);
        up = PosD.Normalized();
        double ground = Ground(PosD, out _);
        double above = PosD.Length() - Geo.Radius - ground;
        if (above <= 0.02)
        {
            if (!Grounded && _vUp < -6) { Impact = Math.Max(Impact, Math.Clamp(-_vUp / 15, 0.2, 1)); if (_vUp < -9) Notice = Loc.T("Atterrissage brutal !"); }
            PosD = up * (Geo.Radius + ground);
            _vUp = Math.Clamp(slopeF * V, -60, 2.5); // on suit le relief (la suspension amortit les petites marches)
            Grounded = true;
        }
        else if (above > 0.08) Grounded = false;

        // Orientation visuelle : la caisse suit la pente, en douceur
        var normal = Grounded ? (up - head * slopeF - right * slopeR).Normalized() : up;
        double k = Math.Min(1, (Grounded ? 10 : 2) * h);
        Up = (Up * (1 - k) + normal * k).Normalized();
        Fwd = Tangent(head, Up);

        WheelAngle += V * h / Spec.WheelRadius;
        if (Bike && input.Throttle > 0 && Grounded) Spin += h * (input.Boost ? 9 : 6) * Math.Min(1, 0.3 + Math.Abs(V) / 6);
    }

    /// <summary>Angle signé (rad) pour aller de <paramref name="a"/> à <paramref name="b"/> autour de la verticale (+ = vers la gauche).</summary>
    static double SignedAngle(Vec3d a, Vec3d b, Vec3d up) => Math.Atan2(a.Cross(b).Dot(up), a.Dot(b));

    /// <summary>Boîte automatique : rapport selon la vitesse, régime dans le rapport (pour le son et le compte-tours).</summary>
    void Gearbox(VehicleInput input, double dt)
    {
        double speed = Math.Abs(V);
        double r = Math.Clamp(speed / Spec.TopSpeed, 0, 1);
        int gears = Math.Max(1, Spec.Gears);
        double target;
        if (Bike)
        {
            Gear = 1;
            target = input.Throttle > 0 ? 0.3 + 0.7 * Math.Min(1, r * 1.2) : 0;
        }
        else
        {
            // rapports plus courts en bas (répartition en racine)
            int g = Math.Clamp((int)Math.Floor(Math.Sqrt(r) * gears) + 1, 1, gears);
            if (V < -0.3) g = -1;
            if (g != Gear && g > 0 && Gear > 0) _shiftTimer = 0.25;
            Gear = g;
            double lo = g <= 1 ? 0 : Math.Pow((double)(g - 1) / gears, 2), hi = Math.Pow((double)Math.Max(g, 1) / gears, 2);
            double f = g < 0 ? Math.Clamp(speed / Math.Max(Spec.Reverse, 1), 0, 1) : Math.Clamp((r - lo) / Math.Max(hi - lo, 1e-3), 0, 1);
            target = 0.18 + 0.72 * f;
            if (input.Throttle > 0 && speed < 1) target = 0.35 + 0.15 * input.Throttle; // embrayage au départ
            if (Throttle01 <= 0) target *= 0.85;
            if (Slip > 0.4 && input.Brake && input.Throttle > 0) target = 0.95; // glissade gaz à fond
            if (_inWater) target = 0.9;
        }
        if (_shiftTimer > 0) { _shiftTimer -= dt; target *= 0.75; }
        _rpm += (target - _rpm) * Math.Min(1, dt * (target > _rpm ? 5 : 3));
        Rpm01 = _rpm;
    }

    public override string Hud()
    {
        string gear = Bike ? "" : $"   {Loc.T("rapport")} {(Gear < 0 ? "R" : Gear.ToString())}";
        string s = $"{Loc.T(Spec.Name)} : {Math.Abs(V) * 3.6:N0} km/h{gear}   {Loc.T("pente")} {_slope * 100:+0;-0;0} %";
        if (!Grounded) s += "   " + Loc.T("(en l'air)");
        if (Slip > 0.3) s += "   " + Loc.T("dérapage !");
        if (_inWater) s += "   " + Loc.T(Bike ? "dans l'eau !" : "dans l'eau : le moteur est noyé !");
        string keys = Bike ? "Z pédaler, Maj en danseuse, S freiner / reculer, Q/D tourner, V vue, E descendre"
            : TwoWheels ? "Z accélérer, S freiner / reculer, Q/D tourner, Espace frein arrière, V vue, E descendre"
            : "Z accélérer, S freiner / reculer, Q/D tourner, Espace frein à main (glisser), V vue, E descendre";
        return s + "   [" + Loc.T(keys) + "]";
    }
}
