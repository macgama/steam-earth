using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Voiture ou vélo : un « modèle bicyclette » (roues avant directrices) sur le vrai relief.
/// Forces le long du cap : moteur (ou jambes) limité en force et en puissance, pente (gravité radiale),
/// résistance au roulement, traînée de l'air (avec le vent réel de face ou de dos), freins.
/// Le virage est limité par l'adhérence des pneus (au-delà, le véhicule sous-vire).
/// Le véhicule quitte le sol sur une bosse si le relief descend plus vite que la gravité ne le ramène.
/// </summary>
public partial class GroundVehicle : Vehicle
{
    readonly bool _bike;
    // Caractéristiques
    readonly double _mass, _maxForce, _power, _boostPower, _cdA, _crr, _brake, _maxReverse, _grip;
    readonly double _wheelbase, _track, _maxSteer, _steerFade, _wheelR, _radius;

    // État
    public double V;            // vitesse le long du cap (m/s, négative en marche arrière)
    public bool Grounded = true;
    Vec3d _heading = new(0, 0, -1);
    double _vUp, _steer, _yawRate, _lean, _wheelAngle, _pedal, _slope;
    bool _inWater;

    // Modèle
    Node3D _frontL, _frontR, _hubFL, _hubFR, _hubRL, _hubRR, _leanPivot, _legL, _legR, _rider, _bars;

    public GroundVehicle(bool bike)
    {
        _bike = bike;
        if (bike)
        {
            // Vélo de ville + cycliste : 85 kg, 250 W (700 W en danseuse avec Maj), ~30 km/h sur le plat
            _mass = 85; _maxForce = 260; _power = 250; _boostPower = 700; _cdA = 0.5; _crr = 0.007;
            _brake = 650; _maxReverse = 0; _grip = 0.85;
            _wheelbase = 1.05; _track = 0.1; _maxSteer = 0.6; _steerFade = 6; _wheelR = 0.34; _radius = 0.45;
        }
        else
        {
            // Citadine : 1 300 kg, 90 kW, ~200 km/h en pointe, 0–100 km/h en ~10 s
            _mass = 1300; _maxForce = 5200; _power = 90_000; _boostPower = 90_000; _cdA = 0.68; _crr = 0.013;
            _brake = 11_000; _maxReverse = 8; _grip = 0.95;
            _wheelbase = 2.6; _track = 1.5; _maxSteer = 0.6; _steerFade = 14; _wheelR = 0.32; _radius = 1.25;
        }
    }

    public override VehicleKind Kind => _bike ? VehicleKind.Bike : VehicleKind.Car;
    public override string Label => _bike ? "Vélo" : "Voiture";
    public override double Speed => V;
    public override bool CanExit => Math.Abs(V) < 2.5;
    public override double Radius => _radius;
    public override double BodyHeight => _bike ? 1.8 : 1.5;
    public override (double halfLength, double halfWidth, double height) Footprint => _bike ? (0.85, 0.3, 1.0) : (2.1, 0.9, 1.5);
    public override double CameraTargetHeight => _bike ? 1.4 : 1.3;
    public override double CameraDistance => _bike ? 4.5 : 7.5;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        _heading = Fwd;
        V = 0; _vUp = 0; _steer = 0; Grounded = true;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(V) * dt / 0.5), 1, 24);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
    }

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        var head = Tangent(_heading, up);
        var right = head.Cross(up).Normalized();

        // Relief sous les roues : pente avant/arrière et gauche/droite
        double hc = Ground(PosD, out bool water);
        double hf = Ground(PosD + head * (_wheelbase / 2), out _), hb = Ground(PosD - head * (_wheelbase / 2), out _);
        double slopeF = (hf - hb) / _wheelbase, slopeR = 0;
        if (!_bike)
        {
            double hr = Ground(PosD + right * (_track / 2), out _), hl = Ground(PosD - right * (_track / 2), out _);
            slopeR = (hr - hl) / _track;
        }
        _slope = slopeF;
        _inWater = water;

        if (Grounded)
        {
            double thr = input.Throttle;
            double drive = 0, opposing = 0;
            double power = input.Boost ? _boostPower : _power;
            if (_inWater) thr *= 0.1; // les roues patinent dans l'eau, le moteur se noie
            if (thr > 0)
            {
                if (V >= -0.3) drive = thr * Math.Min(_maxForce, power / Math.Max(Math.Abs(V), 1));
                else opposing += _brake * thr; // on freine la marche arrière avant de repartir
            }
            else if (thr < 0)
            {
                if (V > 0.3 || _maxReverse <= 0) opposing += _brake * -thr;
                else if (V > -_maxReverse) drive = thr * Math.Min(_maxForce * 0.6, power * 0.5 / Math.Max(Math.Abs(V), 1));
            }
            if (input.Brake) opposing += _brake * 0.8;

            double sinSlope = slopeF / Math.Sqrt(1 + slopeF * slopeF);
            double windAlong = Wind(PosD, 2).Dot(head);
            double air = V - windAlong;
            double force = drive - _mass * G * sinSlope - 0.5 * 1.2 * _cdA * air * Math.Abs(air);
            opposing += _crr * _mass * G * (_inWater ? 25 : 1);

            V += force / _mass * h;
            // Le roulement et les freins s'opposent au mouvement sans jamais l'inverser (adhérence statique)
            double dv = opposing / _mass * h;
            V = Math.Abs(V) <= dv ? 0 : V - Math.Sign(V) * dv;

            // Direction : moins de braquage à haute vitesse, virage limité par l'adhérence
            double maxSteer = _maxSteer / (1 + (V / _steerFade) * (V / _steerFade));
            double target = input.Steer * maxSteer;
            _steer += Math.Clamp(target - _steer, -2.5 * h, 2.5 * h);
            _yawRate = V * Math.Tan(_steer) / _wheelbase;
            double maxYaw = _grip * G / Math.Max(Math.Abs(V), 0.5);
            _yawRate = Math.Clamp(_yawRate, -maxYaw, maxYaw);
            head = Rotate(head, up, -_yawRate * h);
        }

        // Avance, puis glissement le long des obstacles (murs, troncs, véhicules garés)
        var moved = PosD + head * (V * h);
        var resolved = Obstacles.Resolve(moved, _radius, BodyHeight);
        if ((resolved - moved).Length() > 1e-4)
        {
            if (Math.Abs(V) > 4) Notice = "Choc !";
            V *= Math.Max(0, 1 - 6 * h);
        }
        PosD = resolved;

        // Vertical : gravité, contact avec le sol
        _vUp -= G * h;
        PosD += PosD.Normalized() * (_vUp * h);
        up = PosD.Normalized();
        double ground = Ground(PosD, out _);
        double above = PosD.Length() - Geo.Radius - ground;
        if (above <= 0.02)
        {
            if (!Grounded && _vUp < -9) Notice = "Atterrissage brutal !";
            PosD = up * (Geo.Radius + ground);
            // on suit le relief (une suspension amortit les petites marches vers le haut)
            _vUp = Math.Clamp(slopeF * V, -60, 2.5);
            Grounded = true;
        }
        else if (above > 0.08) Grounded = false;

        // Orientation visuelle : la caisse suit la pente, en douceur
        _heading = head;
        var normal = Grounded ? (up - head * slopeF - right * slopeR).Normalized() : up;
        double k = Math.Min(1, (Grounded ? 10 : 2) * h);
        Up = (Up * (1 - k) + normal * k).Normalized();
        Fwd = Tangent(head, Up);

        _wheelAngle += V * h / _wheelR;
        if (_bike)
        {
            double leanTarget = Math.Clamp(Math.Atan(V * _yawRate / G), -0.8, 0.8);
            _lean += (leanTarget - _lean) * Math.Min(1, 6 * h);
            if (input.Throttle > 0 && Grounded) _pedal += h * (input.Boost ? 9 : 6) * Math.Min(1, 0.3 + V / 6);
        }
    }

    public override string Hud()
    {
        string s = $"{Label} : {V * 3.6:N0} km/h   pente {_slope * 100:+0;-0;0} %";
        if (!Grounded) s += "   (en l'air)";
        if (_inWater) s += _bike ? "   dans l'eau !" : "   dans l'eau : le moteur est noyé !";
        s += _bike
            ? "   [Z pédaler, Maj en danseuse, S freiner, Q/D tourner, E descendre]"
            : "   [Z accélérer, S freiner / reculer, Q/D tourner, Espace frein à main, E descendre]";
        return s;
    }

    // --- Modèles 3D (formes simples). Godot : avant = −Z, haut = +Y, origine au sol.

    public override void _Ready()
    {
        if (_bike) BuildBike(); else BuildCar();
    }

    void BuildCar()
    {
        var palette = new[] { new Color(0.75f, 0.1f, 0.1f), new Color(0.12f, 0.28f, 0.62f), new Color(0.9f, 0.9f, 0.88f), new Color(0.2f, 0.2f, 0.22f), new Color(0.15f, 0.45f, 0.3f) };
        var paint = Mat(palette[(int)(GD.Randi() % palette.Length)], 0.35f, 0.4f);
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.16f, 0.2f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.1f, Metallic = 0.3f };
        var tyre = Mat(new Color(0.06f, 0.06f, 0.06f), 0.9f);
        var rim = Mat(new Color(0.7f, 0.7f, 0.72f), 0.3f, 0.8f);
        var light = new StandardMaterial3D { AlbedoColor = new Color(1, 0.97f, 0.85f), EmissionEnabled = true, Emission = new Color(1, 0.95f, 0.8f), EmissionEnergyMultiplier = 1.5f };
        var red = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.05f, 0.05f), EmissionEnabled = true, Emission = new Color(0.6f, 0, 0) };
        var dark = Mat(new Color(0.08f, 0.08f, 0.08f), 0.8f);

        Part(new BoxMesh { Size = new Vector3(1.74f, 0.62f, 4.1f) }, paint, new Vector3(0, 0.62f, 0));            // caisse
        Part(new BoxMesh { Size = new Vector3(1.6f, 0.56f, 2.1f) }, glass, new Vector3(0, 1.21f, 0.25f));          // vitres
        Part(new BoxMesh { Size = new Vector3(1.62f, 0.06f, 1.9f) }, paint, new Vector3(0, 1.5f, 0.3f));           // toit
        Part(new BoxMesh { Size = new Vector3(1.78f, 0.18f, 0.12f) }, dark, new Vector3(0, 0.42f, -2.06f));        // pare-chocs
        Part(new BoxMesh { Size = new Vector3(1.78f, 0.18f, 0.12f) }, dark, new Vector3(0, 0.42f, 2.06f));
        Part(new BoxMesh { Size = new Vector3(0.36f, 0.12f, 0.05f) }, light, new Vector3(-0.62f, 0.72f, -2.06f));  // phares
        Part(new BoxMesh { Size = new Vector3(0.36f, 0.12f, 0.05f) }, light, new Vector3(0.62f, 0.72f, -2.06f));
        Part(new BoxMesh { Size = new Vector3(0.32f, 0.1f, 0.05f) }, red, new Vector3(-0.64f, 0.75f, 2.06f));      // feux arrière
        Part(new BoxMesh { Size = new Vector3(0.32f, 0.1f, 0.05f) }, red, new Vector3(0.64f, 0.75f, 2.06f));

        _frontL = Wheel(new Vector3(-0.78f, 0.32f, -1.3f), 0.32f, 0.22f, tyre, rim, this, out _hubFL);
        _frontR = Wheel(new Vector3(0.78f, 0.32f, -1.3f), 0.32f, 0.22f, tyre, rim, this, out _hubFR);
        Wheel(new Vector3(-0.78f, 0.32f, 1.3f), 0.32f, 0.22f, tyre, rim, this, out _hubRL);
        Wheel(new Vector3(0.78f, 0.32f, 1.3f), 0.32f, 0.22f, tyre, rim, this, out _hubRR);

        _rider = Rider(new Vector3(-0.4f, 0.62f, 0.25f), out _legL, out _legR);
        _legL.Rotation = _legR.Rotation = new Vector3(1.35f, 0, 0); // jambes vers les pédales
    }

    void BuildBike()
    {
        var frame = Mat(new Color(0.1f, 0.55f, 0.75f), 0.4f, 0.5f);
        var tyre = Mat(new Color(0.06f, 0.06f, 0.06f), 0.9f);
        var steel = Mat(new Color(0.75f, 0.75f, 0.78f), 0.3f, 0.9f);
        var dark = Mat(new Color(0.1f, 0.1f, 0.1f), 0.7f);

        _leanPivot = Pivot(Vector3.Zero);
        // Roues (tores) : avant directrice
        var torus = new TorusMesh { InnerRadius = 0.29f, OuterRadius = 0.35f, Rings = 24, RingSegments = 8 };
        _frontL = Pivot(new Vector3(0, 0.35f, -0.53f), _leanPivot);
        _hubFL = Pivot(Vector3.Zero, _frontL);
        Part(torus, tyre, Vector3.Zero, _hubFL, new Vector3(0, 0, Mathf.Pi / 2));
        Part(new BoxMesh { Size = new Vector3(0.02f, 0.6f, 0.02f) }, steel, Vector3.Zero, _hubFL);
        Part(new BoxMesh { Size = new Vector3(0.02f, 0.02f, 0.6f) }, steel, Vector3.Zero, _hubFL);
        var rear = Pivot(new Vector3(0, 0.35f, 0.53f), _leanPivot);
        _hubRL = Pivot(Vector3.Zero, rear);
        Part(torus, tyre, Vector3.Zero, _hubRL, new Vector3(0, 0, Mathf.Pi / 2));
        Part(new BoxMesh { Size = new Vector3(0.02f, 0.6f, 0.02f) }, steel, Vector3.Zero, _hubRL);
        Part(new BoxMesh { Size = new Vector3(0.02f, 0.02f, 0.6f) }, steel, Vector3.Zero, _hubRL);

        // Cadre : tubes (boîtes fines) entre les points clés
        var crank = new Vector3(0, 0.33f, 0.02f);
        var seat = new Vector3(0, 0.95f, 0.2f);
        var head = new Vector3(0, 0.9f, -0.42f);
        Tube(crank, seat, frame); Tube(crank, head, frame); Tube(seat, head + new Vector3(0, 0.02f, 0), frame);
        Tube(crank, new Vector3(0, 0.35f, 0.53f), frame); Tube(seat, new Vector3(0, 0.35f, 0.53f), frame);
        // Fourche et guidon tournent avec la roue avant
        _bars = Pivot(head, _leanPivot);
        Tube(Vector3.Zero, new Vector3(0, -0.55f, -0.11f), steel, _bars);
        Part(new BoxMesh { Size = new Vector3(0.56f, 0.03f, 0.03f) }, dark, new Vector3(0, 0.14f, 0.04f), _bars);
        Tube(Vector3.Zero, new Vector3(0, 0.14f, 0.04f), steel, _bars);
        Part(new BoxMesh { Size = new Vector3(0.14f, 0.05f, 0.26f) }, dark, seat + new Vector3(0, 0.03f, 0), _leanPivot); // selle

        _rider = Rider(seat + new Vector3(0, 0.05f, 0), out _legL, out _legR, 0.55f);
        RemoveChild(_rider);
        _leanPivot.AddChild(_rider);
    }

    void Tube(Vector3 a, Vector3 b, Material mat, Node3D parent = null)
    {
        var d = b - a;
        var mi = Part(new CylinderMesh { TopRadius = 0.022f, BottomRadius = 0.022f, Height = d.Length(), RadialSegments = 8 }, mat, (a + b) / 2, parent ?? _leanPivot);
        mi.Basis = new Basis(new Quaternion(Vector3.Up, d.Normalized()));
    }

    protected override void Animate()
    {
        if (_rider != null) _rider.Visible = Occupied;
        float w = -(float)(_wheelAngle % (2 * Math.PI));
        foreach (var hub in new[] { _hubFL, _hubFR, _hubRL, _hubRR }) if (hub != null) hub.Rotation = new Vector3(w, 0, 0);
        if (_frontL != null) _frontL.Rotation = new Vector3(0, -(float)_steer, 0);
        if (_frontR != null) _frontR.Rotation = new Vector3(0, -(float)_steer, 0);
        if (_bike)
        {
            _leanPivot.Rotation = new Vector3(0, 0, -(float)_lean);
            _bars.Rotation = new Vector3(0, -(float)_steer, 0);
            float a = (float)Math.Sin(_pedal) * 0.55f;
            _legL.Rotation = new Vector3(0.5f + a, 0, 0);
            _legR.Rotation = new Vector3(0.5f - a, 0, 0);
        }
    }
}
