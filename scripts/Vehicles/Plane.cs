using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Petit avion de tourisme quadriplace à aile haute (≈ 1 100 kg, 11 m d'envergure, moteur de 180 ch).
/// Modèle de vol : portance et traînée calculées à partir de la vitesse <b>par rapport à l'air</b>
/// (vitesse sol − vent réel GFS à l'altitude de l'avion), densité de l'air qui baisse avec l'altitude,
/// décrochage au-delà de ~15° d'incidence, poussée d'hélice limitée par la puissance.
/// Les commandes règlent l'incidence voulue (profondeur), le taux de roulis (ailerons) et le lacet (palonnier) ;
/// leur efficacité dépend de la pression dynamique, comme sur un vrai avion. La stabilité de girouette aligne
/// le nez sur le vent relatif : par vent de travers, l'avion « crabe ».
/// Au sol : roues (roulement, freins, roue avant directrice) ; atterrissage trop dur, trop incliné ou sur l'eau = crash.
/// </summary>
public partial class Plane : Vehicle
{
    const double Mass = 1100, WingArea = 16.2;
    const double CL0 = 0.28, CLAlpha = 5.0, StallAlpha = 0.27, CD0 = 0.032, InducedK = 0.05;
    const double StaticThrust = 3300, ThrustPower = 96_000; // W utiles à l'hélice (≈ 135 kW × 80 %)

    public Vec3d Vel;
    public double Throttle;
    public bool OnGround = true;
    double _alpha, _beta, _airspeed, _prop, _elev, _ail, _rud;
    Vec3d _wind;
    Node3D _propeller, _elevator, _aileronL, _aileronR, _rudderNode, _nose, _rider;

    public override VehicleKind Kind => VehicleKind.Plane;
    public override string Label => "Avion";
    public override double Speed => _airspeed;
    public override bool CanExit => OnGround && Vel.Length() < 2;
    public override double Radius => 5.5;
    public override double BodyHeight => 2.8;
    public override (double halfLength, double halfWidth, double height) Footprint => (4.6, 0.7, 2.1); // sous l'aile haute, on passe
    public override double CameraTargetHeight => 1.9;
    public override double CameraDistance => 17;
    public override double CameraPitch => -0.18;
    public override Vec3d ExitPoint => PosD - Right * 2.2 + Fwd * 0.5;
    public bool Stalling => !OnGround && Math.Abs(_alpha) > StallAlpha * 0.92;

    public override void PlaceAt(Vec3d pos, Vec3d heading)
    {
        base.PlaceAt(pos, heading);
        Vel = new Vec3d(0, 0, 0);
        Throttle = 0; OnGround = true;
    }

    /// <summary>Met l'avion en vol en <paramref name="pos"/>, à <paramref name="speed"/> m/s, en palier.</summary>
    public void PlaceInAir(Vec3d pos, Vec3d heading, double speed)
    {
        var up = pos.Normalized();
        PosD = pos; Up = up; Fwd = Tangent(heading, up);
        Vel = Fwd * speed;
        Throttle = 0.65; OnGround = false;
    }

    public override void Step(VehicleInput input, double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(dt * 120), 1, 12);
        for (int i = 0; i < n; i++) SubStep(input, dt / n);
    }

    void SubStep(VehicleInput input, double h)
    {
        var up = PosD.Normalized();
        double alt = PosD.Length() - Geo.Radius;
        if (input.ThrottleUp) Throttle = Math.Min(1, Throttle + 0.5 * h);
        if (input.ThrottleDown) Throttle = Math.Max(0, Throttle - 0.5 * h);

        double ground = Ground(PosD, out _);
        double agl = Math.Max(2, alt - ground);
        _wind = Wind(PosD, agl);
        var vA = Vel - _wind;
        double v = vA.Length();
        _airspeed = v;
        var right = Right;
        double rho = 1.225 * Math.Exp(-Math.Max(0, alt) / 8500);
        double q = 0.5 * rho * v * v;

        _alpha = v > 1 ? Math.Atan2(-vA.Dot(Up), vA.Dot(Fwd)) : 0;
        _beta = v > 1 ? Math.Asin(Math.Clamp(vA.Dot(right) / v, -1, 1)) : 0;

        // Coefficients aérodynamiques (décrochage au-delà de StallAlpha)
        double a = Math.Abs(_alpha);
        double cl = CL0 + CLAlpha * _alpha;
        double cd = CD0;
        if (a > StallAlpha)
        {
            double clMax = CL0 + CLAlpha * StallAlpha * Math.Sign(_alpha);
            double fade = Math.Clamp(1 - (a - StallAlpha) / 0.25, 0, 1);
            cl = clMax * fade + (1 - fade) * 0.9 * Math.Sin(2 * _alpha);
            cd += 1.1 * Math.Sin(a) * Math.Sin(a);
        }
        cd += InducedK * cl * cl;

        var acc = up * -G;
        if (v > 1)
        {
            var vhat = vA * (1 / v);
            var liftDir = right.Cross(vhat).Normalized();
            acc += (liftDir * (q * WingArea * cl) - vhat * (q * WingArea * cd) - right * (q * WingArea * 0.5 * _beta)) * (1 / Mass);
        }
        double thrust = Throttle * Math.Min(StaticThrust, ThrustPower / Math.Max(v, 1)) * rho / 1.225;
        acc += Fwd * (thrust / Mass);
        Vel += acc * h;

        // Au sol : roulement et freins le long de l'axe, les roues empêchent de glisser de côté
        double groundSpeed = 0;
        if (OnGround)
        {
            var ft = Tangent(Fwd, up);
            double vf = Vel.Dot(ft);
            double dv = (0.03 + (input.Brake ? 0.45 : 0)) * G * h;
            double nvf = Math.Abs(vf) <= dv ? 0 : vf - Math.Sign(vf) * dv;
            Vel -= ft * (vf - nvf);
            var rt = ft.Cross(up).Normalized();
            Vel -= rt * (Vel.Dot(rt) * Math.Min(1, 12 * h));
            groundSpeed = Math.Abs(nvf);
        }

        // Rotations : efficacité des gouvernes ∝ pression dynamique (référence : 50 m/s au niveau de la mer)
        double eff = Math.Clamp(q / 1500, 0, 1.3);
        double pitchIn = -input.Throttle; // S = tirer (cabrer), Z = pousser
        double alphaTarget = 0.035 + pitchIn * (pitchIn > 0 ? 0.24 : 0.14);
        double pitchRate = (alphaTarget - _alpha) * 2.2 * eff;
        double bankRight = -Math.Asin(Math.Clamp(right.Dot(up), -1, 1));
        double rollRate = OnGround ? 0 : (input.Steer * 1.5 - bankRight * 0.08) * eff;
        double yawRate = (_beta * 1.8 + input.Rudder * 0.35) * eff;
        if (OnGround) yawRate += (input.Steer + input.Rudder) * 0.5 * Math.Clamp(groundSpeed / 2.5, 0, 1) / (1 + groundSpeed / 20);
        _elev += (pitchIn - _elev) * Math.Min(1, 8 * h);
        _ail += (input.Steer - _ail) * Math.Min(1, 8 * h);
        _rud += (input.Rudder + (OnGround ? input.Steer : 0) - _rud) * Math.Min(1, 8 * h);

        var f = Fwd; var u = Up;
        f = Rotate(f, right, pitchRate * h); u = Rotate(u, right, pitchRate * h);   // tangage (+ = cabrer)
        u = Rotate(u, f, rollRate * h);                                            // roulis (+ = aile droite basse)
        f = Rotate(f, u, -yawRate * h);                                            // lacet (+ = nez à droite)
        Fwd = f.Normalized();
        Up = (u - Fwd * u.Dot(Fwd)).Normalized();

        PosD += Vel * h;

        // Contact avec le sol (le point de référence est sous les roues)
        up = PosD.Normalized();
        ground = Ground(PosD, out bool water);
        double above = PosD.Length() - Geo.Radius - ground;
        if (above <= 0)
        {
            double vn = Vel.Dot(up);
            if (!OnGround)
            {
                double bank = Math.Abs(Math.Asin(Math.Clamp(Right.Dot(up), -1, 1)));
                if (water) { Crash($"Amerrissage à {Vel.Length() * 3.6:N0} km/h : cet avion n'a pas de flotteurs !"); return; }
                if (vn < -4.5 || bank > 0.4 || Fwd.Dot(up) < -0.2) { Crash($"Crash ! (vitesse verticale {vn:F1} m/s, inclinaison {bank * 180 / Math.PI:F0}°)"); return; }
                Notice = $"Posé ! (vitesse verticale {vn:F1} m/s)";
            }
            PosD = up * (Geo.Radius + ground);
            if (vn < 0) Vel -= up * vn;
            OnGround = true;
        }
        else if (above > 0.15) OnGround = false;

        if (OnGround) AlignOnGround(up);
        else
        {
            // Bâtiments : l'aile n'y résiste pas
            var resolved = Obstacles.Resolve(PosD, 4, 3);
            if ((resolved - PosD).Length() > 1e-3) { Crash("Collision avec un bâtiment !"); return; }
        }
        _prop += (Throttle * 0.85 + 0.15 * Math.Min(1, Throttle * 10 + 0.3)) * 160 * h;
    }

    /// <summary>Au sol : pas d'inclinaison latérale, le nez ne descend pas sous l'horizontale de la piste,
    /// la queue ne touche pas (cabrage limité à 14°).</summary>
    void AlignOnGround(Vec3d up)
    {
        var ft = Tangent(Fwd, up);
        var rt = ft.Cross(up).Normalized();
        double hf = Ground(PosD + ft * 2.5, out _), hb = Ground(PosD - ft * 1.0, out _);
        double hr = Ground(PosD + rt * 1.3, out _), hl = Ground(PosD - rt * 1.3, out _);
        var n = (up - ft * ((hf - hb) / 3.5) - rt * ((hr - hl) / 2.6)).Normalized();
        var fn = Tangent(Fwd, n);
        double p = Math.Clamp(Math.Asin(Math.Clamp(Fwd.Dot(n), -1, 1)), 0, 0.25);
        Fwd = (fn * Math.Cos(p) + n * Math.Sin(p)).Normalized();
        var r = Fwd.Cross(n).Normalized();
        Up = r.Cross(Fwd).Normalized();
    }

    void Crash(string message)
    {
        Notice = message;
        var up = PosD.Normalized();
        PosD = up * (Geo.Radius + Ground(PosD, out _));
        Vel = new Vec3d(0, 0, 0);
        Throttle = 0;
        OnGround = true;
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
        string s = $"Avion : vitesse air {_airspeed * 3.6:N0} km/h, sol {gs * 3.6:N0} km/h   altitude {alt:N0} m (hauteur {HeightAboveGround:N0} m)   " +
                   $"vario {Vel.Dot(up):+0.0;-0.0;0.0} m/s   gaz {Throttle * 100:N0} %   incidence {_alpha * 180 / Math.PI:F0}°   vent réel {ws * 3.6:N0} km/h du {from:F0}°";
        if (Stalling) s += "   ⚠ DÉCROCHAGE";
        if (OnGround) s += Vel.Length() < 2 ? "   (au sol)" : "   (roulage)";
        return s + "   [Maj/Ctrl gaz, S cabrer, Z piquer, Q/D roulis (au sol : diriger), ←/→ palonnier, Espace freins, E descendre]";
    }

    public override void _Ready()
    {
        var white = Mat(new Color(0.95f, 0.95f, 0.95f), 0.4f);
        var red = Mat(new Color(0.75f, 0.1f, 0.1f), 0.4f);
        var dark = Mat(new Color(0.1f, 0.1f, 0.12f), 0.6f);
        var tyre = Mat(new Color(0.06f, 0.06f, 0.06f), 0.9f);
        var metal = Mat(new Color(0.7f, 0.7f, 0.72f), 0.3f, 0.8f);
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.2f, 0.26f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.1f };

        // Fuselage (axe le long de Z), cabine vitrée, capot moteur
        Part(new CapsuleMesh { Radius = 0.62f, Height = 4.2f }, white, new Vector3(0, 1.55f, -0.6f), null, new Vector3(Mathf.Pi / 2, 0, 0));
        Part(new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.55f, Height = 3.6f }, white, new Vector3(0, 1.7f, 3.0f), null, new Vector3(Mathf.Pi / 2, 0, 0));
        Part(new BoxMesh { Size = new Vector3(1.15f, 0.6f, 1.6f) }, glass, new Vector3(0, 2.05f, -0.5f));
        Part(new BoxMesh { Size = new Vector3(1.28f, 0.12f, 3.0f) }, red, new Vector3(0, 1.3f, 0.6f));
        _nose = Pivot(new Vector3(0, 1.55f, -2.75f));
        Part(new SphereMesh { Radius = 0.2f, Height = 0.4f }, red, Vector3.Zero, _nose);
        _propeller = Pivot(new Vector3(0, 0, -0.1f), _nose);
        Part(new BoxMesh { Size = new Vector3(1.9f, 0.14f, 0.04f) }, dark, Vector3.Zero, _propeller);

        // Aile haute, ailerons, empennage
        Part(new BoxMesh { Size = new Vector3(11f, 0.14f, 1.5f) }, white, new Vector3(0, 2.4f, -0.4f));
        Part(new BoxMesh { Size = new Vector3(0.6f, 0.15f, 1.52f) }, red, new Vector3(-5.2f, 2.4f, -0.4f));
        Part(new BoxMesh { Size = new Vector3(0.6f, 0.15f, 1.52f) }, red, new Vector3(5.2f, 2.4f, -0.4f));
        _aileronL = Pivot(new Vector3(-3.8f, 2.4f, 0.35f));
        Part(new BoxMesh { Size = new Vector3(2.2f, 0.06f, 0.3f) }, white, new Vector3(0, 0, 0.15f), _aileronL);
        _aileronR = Pivot(new Vector3(3.8f, 2.4f, 0.35f));
        Part(new BoxMesh { Size = new Vector3(2.2f, 0.06f, 0.3f) }, white, new Vector3(0, 0, 0.15f), _aileronR);
        Part(new BoxMesh { Size = new Vector3(0.06f, 1.0f, 0.06f) }, metal, new Vector3(-1.2f, 1.75f, -0.3f), null, new Vector3(0, 0, 0.9f)); // haubans
        Part(new BoxMesh { Size = new Vector3(0.06f, 1.0f, 0.06f) }, metal, new Vector3(1.2f, 1.75f, -0.3f), null, new Vector3(0, 0, -0.9f));
        Part(new BoxMesh { Size = new Vector3(3.4f, 0.08f, 0.8f) }, white, new Vector3(0, 1.75f, 4.3f));
        _elevator = Pivot(new Vector3(0, 1.75f, 4.7f));
        Part(new BoxMesh { Size = new Vector3(3.2f, 0.05f, 0.35f) }, red, new Vector3(0, 0, 0.17f), _elevator);
        Part(new BoxMesh { Size = new Vector3(0.08f, 1.3f, 1.0f) }, white, new Vector3(0, 2.45f, 4.25f));
        _rudderNode = Pivot(new Vector3(0, 2.45f, 4.75f));
        Part(new BoxMesh { Size = new Vector3(0.06f, 1.25f, 0.35f) }, red, new Vector3(0, 0, 0.17f), _rudderNode);

        // Train d'atterrissage tricycle (le point de référence est sous les roues)
        foreach (var x in new[] { -1.2f, 1.2f })
        {
            Wheel(new Vector3(x, 0.26f, 0.3f), 0.26f, 0.16f, tyre, metal, this, out _);
            Part(new BoxMesh { Size = new Vector3(0.06f, 1.0f, 0.08f) }, metal, new Vector3(x * 0.7f, 0.75f, 0.3f), null, new Vector3(0, 0, x > 0 ? 0.55f : -0.55f));
        }
        Wheel(new Vector3(0, 0.22f, -2.2f), 0.22f, 0.12f, tyre, metal, this, out _);
        Part(new BoxMesh { Size = new Vector3(0.06f, 0.9f, 0.06f) }, metal, new Vector3(0, 0.7f, -2.25f));

        _rider = Rider(new Vector3(-0.3f, 1.3f, -0.2f), CharacterSeat.Plane);
    }

    protected override void Animate()
    {
        UpdateRider();
        if (_propeller == null) return;
        _propeller.Rotation = new Vector3(0, 0, (float)(_prop % (2 * Math.PI)));
        _elevator.Rotation = new Vector3((float)_elev * 0.35f, 0, 0);
        _aileronL.Rotation = new Vector3((float)_ail * 0.3f, 0, 0);
        _aileronR.Rotation = new Vector3(-(float)_ail * 0.3f, 0, 0);
        _rudderNode.Rotation = new Vector3(0, -(float)_rud * 0.4f, 0);
    }
}
