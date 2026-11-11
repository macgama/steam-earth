using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Bateau à moteur (≈ 6 m, 1 400 kg, ~65 km/h) sur les lacs, rivières et la mer.
/// Poussée de l'hélice contre la traînée de la coque ; le gouvernail n'agit que s'il y a de l'eau qui le traverse
/// (vitesse ou souffle de l'hélice). La flottaison est un ressort amorti vers la surface de l'eau
/// (<see cref="WaterSurface"/>, vagues comprises) ; la coque prend l'inclinaison des vagues sous la proue,
/// la poupe et les flancs, se cabre en déjaugeant et s'incline dans les virages. Elle s'échoue sur la rive.
/// </summary>
public partial class Boat : Vehicle
{
    const double Mass = 1400, Thrust = 9000, Length = 6, Beam = 2.3;
    const double QuadDrag = Thrust / (18.0 * 18.0); // vitesse maximale ≈ 18 m/s

    public double V;
    double _vSide, _vUp, _throttle, _rudder, _yawRate, _prop;
    Vec3d _heading = new(0, 0, -1);
    bool _afloat = true, _beached;
    Node3D _motor, _propeller, _rider, _legL, _legR;

    public override VehicleKind Kind => VehicleKind.Boat;
    public override string Label => "Bateau";
    public override double Speed => V;
    public override bool CanExit => Math.Abs(V) < 2;
    public override double Radius => 2.4;
    public override double BodyHeight => 2.0;
    public override double CameraTargetHeight => 1.6;
    public override double CameraDistance => 11;
    public override Vec3d ExitPoint => PosD - Right * (Beam / 2 + 0.6);

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        _heading = Fwd;
        V = 0; _vSide = 0; _vUp = 0; _throttle = 0;
    }

    public override void StopAtBorder() { V = 0; _vSide = 0; _yawRate = 0; }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(V) * dt / 0.5), 1, 16);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
    }

    static double Level(Vec3d p, double fallback) => WaterSurface.At(p, out double l) ? l : fallback;

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        var head = Tangent(_heading, up);
        var right = head.Cross(up).Normalized();
        double alt = PosD.Length() - Geo.Radius;

        // Manette des gaz progressive : Z = plein avant, S = marche arrière (40 %)
        double target = input.Throttle > 0 ? input.Throttle : input.Throttle * 0.4;
        _throttle += Math.Clamp(target - _throttle, -0.8 * h, 0.8 * h);
        _rudder += Math.Clamp(input.Steer - _rudder, -3 * h, 3 * h);

        _afloat = WaterSurface.At(PosD, out double level);
        if (_afloat)
        {
            double force = _throttle * Thrust - QuadDrag * V * Math.Abs(V) - 60 * V;
            V += force / Mass * h;
            // Gouvernail : efficace avec la vitesse ou le souffle de l'hélice ; virage limité (≈ 0,5 g)
            double flow = V + _throttle * 5;
            _yawRate = _rudder * 0.45 * Math.Clamp(flow / 5, -1, 1);
            double maxYaw = 0.5 * G / Math.Max(Math.Abs(V), 1);
            _yawRate = Math.Clamp(_yawRate, -maxYaw, maxYaw);
            head = Rotate(head, up, -_yawRate * h);
            // Dérive : la coque glisse un peu dans les virages et sous le vent
            double windSide = Wind(PosD, 2).Dot(right);
            _vSide += (V * _yawRate * 0.08 + windSide * 0.03 - _vSide * 1.5) * h;
        }
        else
        {
            V *= Math.Max(0, 1 - 8 * h); // à terre, on ne va nulle part
            _vSide = 0;
        }

        // Avance, sauf si la proue touche la rive
        var next = PosD + head * (V * h) + right * (_vSide * h);
        var bow = next + head * (Math.Sign(V == 0 ? 1 : V) * Length / 2);
        _beached = false;
        if (!WaterSurface.At(bow, out _) && Ground(bow, out _) > level + 0.2)
        {
            if (Math.Abs(V) > 3) Notice = "Échoué sur la rive !";
            V *= -0.15; _vSide = 0; _beached = true;
        }
        else
        {
            var resolved = Obstacles.Resolve(next, Radius, BodyHeight);
            if ((resolved - next).Length() > 1e-4) { if (Math.Abs(V) > 4) Notice = "Choc !"; V *= Math.Max(0, 1 - 6 * h); }
            PosD = resolved;
        }

        // Flottaison : ressort amorti vers la surface (vagues comprises)
        up = PosD.Normalized();
        alt = PosD.Length() - Geo.Radius;
        if (WaterSurface.At(PosD, out level))
        {
            _vUp += (14 * (level - alt) - 4 * _vUp) * h;
            PosD += up * (_vUp * h);
        }
        else
        {
            double ground = Ground(PosD, out _);
            PosD = up * (Geo.Radius + Math.Max(ground, alt - 0.5 * h * 9.81));
            if (PosD.Length() - Geo.Radius <= ground + 0.01) _vUp = 0;
        }

        // Assiette : pente des vagues + cabrage en déjaugeant + gîte dans les virages
        double lc = PosD.Length() - Geo.Radius;
        double lf = Level(PosD + head * (Length / 2), lc), lb = Level(PosD - head * (Length / 2), lc);
        double lr = Level(PosD + right * (Beam / 2), lc), ll = Level(PosD - right * (Beam / 2), lc);
        double s = Math.Clamp(Math.Abs(V) / 14, 0, 1);
        double trim = 0.09 * Math.Sin(s * Math.PI) + 0.03 * s;
        double heel = Math.Clamp(V * _yawRate * 0.03, -0.25, 0.25);
        var normal = (up - head * ((lf - lb) / Length + trim) - right * ((lr - ll) / Beam - heel)).Normalized();
        double k = Math.Min(1, 4 * h);
        Up = (Up * (1 - k) + normal * k).Normalized();
        _heading = head;
        Fwd = Tangent(head, Up);
        _prop += _throttle * 60 * h;
    }

    public override string Hud()
    {
        string s = $"Bateau : {V * 3.6:N0} km/h ({V * 1.944:N0} nœuds)   moteur {_throttle * 100:+0;-0;0} %";
        if (_beached) s += "   échoué";
        else if (!_afloat) s += "   à terre";
        return s + "   [Z avant, S arrière, Q/D gouvernail, E descendre]";
    }

    public override void _Ready()
    {
        var hull = Mat(new Color(0.95f, 0.95f, 0.93f), 0.4f);
        var stripe = Mat(new Color(0.08f, 0.25f, 0.55f), 0.4f);
        var deck = Mat(new Color(0.55f, 0.38f, 0.22f), 0.7f);
        var dark = Mat(new Color(0.1f, 0.1f, 0.12f), 0.6f);
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.2f, 0.25f, 0.5f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.1f };
        var seat = Mat(new Color(0.85f, 0.85f, 0.8f), 0.8f);

        // Coque : partie arrière rectangulaire + proue en prisme (pointe vers l'avant), en partie sous l'eau
        Part(new BoxMesh { Size = new Vector3((float)Beam, 0.9f, 4.0f) }, hull, new Vector3(0, 0.15f, 0.9f));
        Part(new PrismMesh { Size = new Vector3((float)Beam, 2.1f, 0.9f), LeftToRight = 0.5f }, hull, new Vector3(0, 0.15f, -2.15f), null, new Vector3(-Mathf.Pi / 2, 0, 0));
        Part(new BoxMesh { Size = new Vector3((float)Beam + 0.02f, 0.12f, 4.0f) }, stripe, new Vector3(0, 0.45f, 0.9f));
        Part(new BoxMesh { Size = new Vector3((float)Beam - 0.2f, 0.05f, 3.8f) }, deck, new Vector3(0, 0.6f, 0.9f));
        Part(new BoxMesh { Size = new Vector3((float)Beam - 0.1f, 0.5f, 0.06f) }, glass, new Vector3(0, 0.95f, -0.6f), null, new Vector3(-0.5f, 0, 0)); // pare-brise
        Part(new BoxMesh { Size = new Vector3(0.6f, 0.7f, 0.5f) }, dark, new Vector3(-0.45f, 0.95f, -0.25f)); // poste de pilotage
        Part(new BoxMesh { Size = new Vector3(1.8f, 0.35f, 0.6f) }, seat, new Vector3(0, 0.78f, 1.9f));        // banquette arrière
        Part(new BoxMesh { Size = new Vector3(0.5f, 0.35f, 0.5f) }, seat, new Vector3(-0.45f, 0.78f, 0.45f)); // siège du pilote
        _motor = Pivot(new Vector3(0, 0.6f, 2.95f));
        Part(new BoxMesh { Size = new Vector3(0.45f, 0.7f, 0.5f) }, dark, new Vector3(0, 0.25f, 0.1f), _motor);
        Part(new BoxMesh { Size = new Vector3(0.12f, 0.9f, 0.2f) }, dark, new Vector3(0, -0.45f, 0.1f), _motor);
        _propeller = Pivot(new Vector3(0, -0.85f, 0.25f), _motor);
        Part(new BoxMesh { Size = new Vector3(0.42f, 0.08f, 0.03f) }, Mat(new Color(0.7f, 0.7f, 0.7f), 0.3f, 0.9f), Vector3.Zero, _propeller);

        _rider = Rider(new Vector3(-0.45f, 0.98f, 0.45f), out _legL, out _legR);
        _legL.Rotation = _legR.Rotation = new Vector3(1.3f, 0, 0);
    }

    protected override void Animate()
    {
        if (_rider != null) _rider.Visible = Occupied;
        if (_motor != null) _motor.Rotation = new Vector3(0, (float)_rudder * 0.5f, 0);
        if (_propeller != null) _propeller.Rotation = new Vector3(0, 0, (float)(_prop % (2 * Math.PI)));
    }
}
