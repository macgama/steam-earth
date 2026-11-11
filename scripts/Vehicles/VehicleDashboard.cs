using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Tableau de bord dessiné en bas de l'écran en vue intérieure (V) : compteurs ronds et voyants selon la famille.
/// Route et deux-roues : compteur de vitesse, compte-tours, rapport, freins, dérapage, pente.
/// Bateaux : vitesse (nœuds), régime ou voiles, cap ; voilier : vent apparent.
/// Avions : badin, horizon artificiel, altimètre, variomètre, cap, gaz, alarme de décrochage.
/// Hélicoptère : vitesse, horizon, altitude, vario, rotor, cap.
/// </summary>
public partial class VehicleDashboard : Control
{
    public Vehicle Vehicle;

    static readonly Color Panel = new(0.05f, 0.06f, 0.08f, 0.82f), FaceCol = new(0.09f, 0.10f, 0.13f, 0.95f),
        Rim = new(0.55f, 0.58f, 0.62f), Tick = new(0.85f, 0.87f, 0.9f), Needle = new(1f, 0.42f, 0.18f),
        Red = new(0.9f, 0.18f, 0.15f), Amber = new(1f, 0.72f, 0.2f), Green = new(0.35f, 0.85f, 0.4f),
        Sky = new(0.25f, 0.55f, 0.9f), Earth = new(0.55f, 0.36f, 0.2f), Dim = new(1, 1, 1, 0.55f);

    Font _font;
    float _s = 1;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _font = ThemeDB.FallbackFont;
    }

    public override void _Process(double delta)
    {
        Visible = Vehicle != null && UiSettings.ShowHud;
        if (Visible) QueueRedraw();
    }

    public override void _Draw()
    {
        var v = Vehicle;
        if (v == null) return;
        var size = GetViewportRect().Size; // parent = CanvasLayer : on se cale sur la fenêtre
        _s = UiSettings.UiScale * Math.Clamp(size.Y / 900f, 0.75f, 1.6f);
        float r = 62 * _s;                    // rayon d'un compteur
        float gap = 18 * _s;
        var spec = v.Spec;
        int count = spec.InAir ? 5 : spec.OnWater ? 3 : spec.Family == VehicleFamily.Bicycle ? 2 : 3;
        float w = count * 2 * r + (count - 1) * gap + 40 * _s, h = 2 * r + 46 * _s;
        var origin = new Vector2((size.X - w) / 2, size.Y - h - 70 * _s); // au-dessus de la ligne d'état du HUD
        var rect = new Rect2(origin, new Vector2(w, h));
        DrawStyleBox(new StyleBoxFlat { BgColor = Panel, CornerRadiusTopLeft = (int)(18 * _s), CornerRadiusTopRight = (int)(18 * _s), CornerRadiusBottomLeft = (int)(18 * _s), CornerRadiusBottomRight = (int)(18 * _s) }, rect);
        var c = new Vector2(origin.X + 20 * _s + r, origin.Y + 14 * _s + r);
        Vector2 Next() { var p = c; c.X += 2 * r + gap; return p; }

        double speed = Math.Abs(v.Speed);
        bool imp = UiSettings.Imperial;
        double toUnit = imp ? 2.23694 : 3.6;
        string unit = UiSettings.SpeedUnit;
        switch (spec.Family)
        {
            case VehicleFamily.Road or VehicleFamily.Motorbike or VehicleFamily.Bicycle:
            {
                double max = Nice(spec.TopSpeed * toUnit * 1.15);
                Gauge(Next(), r, speed * toUnit / max, max, UiSettings.SpeedValue(speed), unit, -1);
                if (spec.Family != VehicleFamily.Bicycle)
                {
                    double maxRpm = spec.Sound == EngineSound.Truck ? 4 : spec.Family == VehicleFamily.Motorbike ? 12 : spec.Id == "sport" ? 9 : 7;
                    Gauge(Next(), r, v.Rpm01, maxRpm, "", Loc.T("×1000 tr/min"), 0.82);
                }
                var p = Next();
                Face(p, r);
                string gear = spec.Family == VehicleFamily.Bicycle ? "" : v.Gear < 0 ? "R" : v.Gear == 0 ? "N" : v.Gear.ToString();
                Text(p + new Vector2(0, -r * 0.18f), gear, 46, Tick);
                if (gear.Length > 0) Text(p + new Vector2(0, r * 0.18f), Loc.T("rapport"), 13, Dim);
                var rv = v as RoadVehicle;
                Lamp(p + new Vector2(-r * 0.45f, r * 0.55f), v.Braking, Red, "●");
                Lamp(p + new Vector2(0, r * 0.55f), v.Slip > 0.3, Amber, "≈");
                Lamp(p + new Vector2(r * 0.45f, r * 0.55f), !v.Grounded, Amber, "↑");
                if (rv != null) Text(p + new Vector2(0, -r * 0.62f), $"{rv.SlopePercent:+0;-0;0} %", 13, Dim);
                break;
            }
            case VehicleFamily.Boat or VehicleFamily.Sailboat:
            {
                var b = (Boat)v;
                double kn = speed * 1.944;
                Gauge(Next(), r, kn / Nice(spec.TopSpeed * 1.944 * 1.2), Nice(spec.TopSpeed * 1.944 * 1.2), $"{kn:N1}", Loc.T("nœuds"), -1);
                if (spec.Family == VehicleFamily.Sailboat)
                {
                    var (ang, ws) = b.ApparentWind;
                    WindDial(Next(), r, ang, ws * 1.944, b.Sails);
                }
                else Gauge(Next(), r, v.Rpm01, 6, $"{v.Throttle01 * 100:+0;-0;0} %", Loc.T("moteur"), 0.9);
                Compass(Next(), r, v.HeadingYaw);
                break;
            }
            case VehicleFamily.Plane or VehicleFamily.Jet:
            {
                var pl = (Plane)v;
                double max = Nice(spec.TopSpeed * toUnit * 1.15);
                Gauge(Next(), r, speed * toUnit / max, max, UiSettings.SpeedValue(speed), unit, spec.StallSpeed * toUnit / max, true);
                Horizon(Next(), r, v.PitchAngle, v.BankAngle);
                Altimeter(Next(), r, v.PosD.Length() - Geo.Radius, v.HeightAboveGround);
                Vario(Next(), r, pl.VerticalSpeed);
                var p = Next();
                Compass(p, r, v.HeadingYaw);
                Bar(new Rect2(p.X + r + 6 * _s, p.Y - r, 10 * _s, 2 * r), pl.Throttle, Loc.T("gaz"));
                if (pl.Stalling && (Time.GetTicksMsec() / 300) % 2 == 0)
                    Text(new Vector2(rect.GetCenter().X, origin.Y - 16 * _s), "⚠ " + Loc.T("DÉCROCHAGE"), 26, Red);
                break;
            }
            case VehicleFamily.Helicopter:
            {
                var hc = (Helicopter)v;
                double max = Nice(spec.TopSpeed * toUnit * 1.15);
                Gauge(Next(), r, speed * toUnit / max, max, UiSettings.SpeedValue(speed), unit, -1);
                Horizon(Next(), r, v.PitchAngle, v.BankAngle);
                Altimeter(Next(), r, v.PosD.Length() - Geo.Radius, v.HeightAboveGround);
                Vario(Next(), r, hc.VerticalSpeed);
                var p = Next();
                Compass(p, r, v.HeadingYaw);
                Bar(new Rect2(p.X + r + 6 * _s, p.Y - r, 10 * _s, 2 * r), hc.Rotor, Loc.T("rotor"));
                break;
            }
        }
    }

    /// <summary>Arrondit l'échelle d'un compteur (60, 80, 100, 120, 160, 200, 240, 300…).</summary>
    static double Nice(double x)
    {
        double[] steps = { 10, 20, 30, 40, 50, 60, 80, 100, 120, 140, 160, 180, 200, 250, 300, 400, 500, 600, 800, 1000, 1200 }; // graduations rondes (voir Gauge)
        foreach (var s in steps) if (s >= x) return s;
        return Math.Ceiling(x / 100) * 100;
    }

    void Face(Vector2 c, float r)
    {
        DrawCircle(c, r, FaceCol);
        DrawArc(c, r, 0, Mathf.Tau, 48, Rim, 2.5f * _s, true);
    }

    void Text(Vector2 center, string s, int size, Color col)
    {
        int fs = (int)(size * _s);
        var sz = _font.GetStringSize(s, HorizontalAlignment.Left, -1, fs);
        DrawString(_font, center + new Vector2(-sz.X / 2, sz.Y * 0.3f), s, HorizontalAlignment.Left, -1, fs, col);
    }

    void Lamp(Vector2 c, bool on, Color col, string sym) => Text(c, sym, 18, on ? col : new Color(1, 1, 1, 0.12f));

    /// <summary>Compteur à aiguille sur 270°, graduations de 0 à <paramref name="max"/>.
    /// <paramref name="mark01"/> : zone rouge (compte-tours) ou trait de décrochage (badin) ; −1 = aucune.</summary>
    void Gauge(Vector2 c, float r, double v01, double max, string value, string unit, double mark01, bool stallMark = false)
    {
        Face(c, r);
        float a0 = Mathf.Pi * 0.75f, span = Mathf.Pi * 1.5f;
        if (mark01 >= 0 && !stallMark) DrawArc(c, r * 0.86f, a0 + span * (float)mark01, a0 + span, 24, Red, 5 * _s, true);
        if (stallMark && mark01 > 0) { DrawArc(c, r * 0.86f, a0, a0 + span * (float)mark01, 24, Red, 3 * _s, true); DrawArc(c, r * 0.86f, a0 + span * (float)mark01, a0 + span * 0.85f, 24, Green, 3 * _s, true); }
        int major = (int)max;
        if (max > 12) foreach (var st in new[] { 10.0, 20, 25, 50, 100, 200, 250 }) { major = (int)Math.Round(max / st); if (major <= 10 && Math.Abs(max - major * st) < 1e-6) break; }
        double stepV = max / major;
        for (int i = 0; i <= major; i++)
        {
            float a = a0 + span * i / major;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(c + d * r * 0.78f, c + d * r * 0.93f, Tick, 2 * _s, true);
            Text(c + d * r * 0.62f, $"{stepV * i:0}", 11, Dim);
            if (i < major)
            {
                float am = a0 + span * (i + 0.5f) / major;
                var dm = new Vector2(Mathf.Cos(am), Mathf.Sin(am));
                DrawLine(c + dm * r * 0.85f, c + dm * r * 0.93f, Dim, 1 * _s, true);
            }
        }
        float an = a0 + span * Mathf.Clamp((float)v01, 0, 1.02f);
        var dn = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
        DrawLine(c - dn * r * 0.12f, c + dn * r * 0.86f, Needle, 3.5f * _s, true);
        DrawCircle(c, 5 * _s, Rim);
        if (value.Length > 0) Text(c + new Vector2(0, r * 0.42f), value, 20, Tick);
        Text(c + new Vector2(0, r * 0.72f), unit, 11, Dim);
    }

    /// <summary>Horizon artificiel : ciel et terre, ligne d'horizon tournée par l'inclinaison et décalée par l'assiette.</summary>
    void Horizon(Vector2 c, float r, double pitch, double bank)
    {
        Face(c, r);
        float rr = r * 0.92f;
        float b = (float)bank;
        var n = new Vector2(Mathf.Sin(b), Mathf.Cos(b));       // vers la terre (écran)
        var lc = c + n * (float)Math.Clamp(pitch, -0.6, 0.6) * rr * 1.6f;
        DrawCircle(c, rr, Sky);
        var ground = new List<Vector2>();
        const int N = 64;
        var pts = new Vector2[N];
        var below = new bool[N];
        for (int i = 0; i < N; i++) { float a = Mathf.Tau * i / N; pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr; below[i] = (pts[i] - lc).Dot(n) > 0; }
        int start = -1;
        for (int i = 0; i < N; i++) if (below[i] && !below[(i + N - 1) % N]) { start = i; break; }
        if (start < 0 && below[0]) DrawCircle(c, rr, Earth);
        else if (start >= 0)
        {
            for (int k = 0; k < N && below[(start + k) % N]; k++) ground.Add(pts[(start + k) % N]);
            if (ground.Count >= 3) DrawColoredPolygon(ground.ToArray(), Earth);
        }
        // graduations d'assiette (tous les 10°)
        var d = new Vector2(Mathf.Cos(b), -Mathf.Sin(b));
        for (int k = -2; k <= 2; k++)
        {
            if (k == 0) continue;
            var p = lc - n * (k * 0.1745f * rr * 1.6f);
            if ((p - c).Length() < rr * 0.8f) DrawLine(p - d * rr * 0.18f, p + d * rr * 0.18f, Tick, 1.5f * _s, true);
        }
        float dist = (lc - c).Dot(n);
        if (Math.Abs(dist) < rr) { float half = Mathf.Sqrt(rr * rr - dist * dist); var mid = c + n * dist; DrawLine(mid - d * half, mid + d * half, Colors.White, 2 * _s, true); }
        DrawArc(c, rr, 0, Mathf.Tau, 48, FaceCol, 7 * _s, true); // recouvre la ligne qui dépasse
        // maquette de l'avion (fixe)
        DrawLine(c + new Vector2(-r * 0.5f, 0), c + new Vector2(-r * 0.15f, 0), Amber, 4 * _s, true);
        DrawLine(c + new Vector2(r * 0.15f, 0), c + new Vector2(r * 0.5f, 0), Amber, 4 * _s, true);
        DrawCircle(c, 4 * _s, Amber);
        Text(c + new Vector2(0, r * 0.72f), $"{bank * 180 / Math.PI:+0;-0;0}°", 11, Tick);
    }

    void Altimeter(Vector2 c, float r, double alt, double agl)
    {
        Face(c, r);
        // aiguille des centaines (un tour = 1 000 m / ft)
        double a = UiSettings.Imperial ? alt * 3.28084 : alt;
        for (int i = 0; i < 10; i++)
        {
            float t = -Mathf.Pi / 2 + Mathf.Tau * i / 10;
            var d = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
            DrawLine(c + d * r * 0.8f, c + d * r * 0.93f, Tick, 2 * _s, true);
            Text(c + d * r * 0.66f, i.ToString(), 11, Dim);
        }
        float an = -Mathf.Pi / 2 + Mathf.Tau * (float)((a % 1000 + 1000) % 1000 / 1000);
        var dn = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
        DrawLine(c, c + dn * r * 0.84f, Needle, 3 * _s, true);
        DrawCircle(c, 4 * _s, Rim);
        Text(c + new Vector2(0, -r * 0.28f), UiSettings.Length(alt), 15, Tick);
        Text(c + new Vector2(0, r * 0.3f), Loc.T("sol") + " " + UiSettings.Length(agl), 11, agl < 50 ? Amber : Dim);
    }

    void Vario(Vector2 c, float r, double vs)
    {
        Face(c, r);
        float a0 = Mathf.Pi; // 0 m/s à gauche, montée vers le haut
        for (int k = -10; k <= 10; k += 5)
        {
            float t = a0 + k / 10f * Mathf.Pi * 0.8f; // + = montée (vers le haut de l'écran)
            var d = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
            DrawLine(c + d * r * 0.78f, c + d * r * 0.93f, Tick, 2 * _s, true);
            Text(c + d * r * 0.62f, Math.Abs(k).ToString(), 11, Dim);
        }
        float an = a0 + (float)Math.Clamp(vs / 10, -1, 1) * Mathf.Pi * 0.8f;
        var dn = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
        DrawLine(c, c + dn * r * 0.84f, Needle, 3 * _s, true);
        DrawCircle(c, 4 * _s, Rim);
        Text(c + new Vector2(r * 0.2f, r * 0.42f), $"{vs:+0.0;-0.0;0.0}", 15, vs < -5 ? Amber : Tick);
        Text(c + new Vector2(r * 0.2f, r * 0.7f), "m/s", 11, Dim);
    }

    /// <summary>Compas : la rose tourne, le cap est en haut.</summary>
    void Compass(Vector2 c, float r, double heading)
    {
        Face(c, r);
        string[] card = { Loc.T("N"), Loc.T("E"), Loc.T("S"), Loc.T("O") };
        for (int i = 0; i < 36; i++)
        {
            float t = (float)(i * Math.PI / 18 - heading) - Mathf.Pi / 2;
            var d = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
            bool major = i % 9 == 0;
            DrawLine(c + d * r * (major ? 0.72f : 0.82f), c + d * r * 0.93f, major ? Tick : Dim, (major ? 2 : 1) * _s, true);
            if (major) Text(c + d * r * 0.58f, card[i / 9], 14, i == 0 ? Red : Tick);
        }
        DrawColoredPolygon(new[] { c + new Vector2(0, -r * 0.98f), c + new Vector2(-6 * _s, -r * 0.8f), c + new Vector2(6 * _s, -r * 0.8f) }, Amber);
        double deg = (heading * 180 / Math.PI + 360) % 360;
        Text(c, $"{deg:000}°", 18, Tick);
    }

    /// <summary>Voilier : vent apparent (flèche autour du bateau vu de dessus), force et voiles hissées.</summary>
    void WindDial(Vector2 c, float r, double angle, double knots, double sails)
    {
        Face(c, r);
        // zone « face au vent » (±35°) en rouge
        DrawArc(c, r * 0.86f, -Mathf.Pi / 2 - 0.6f, -Mathf.Pi / 2 + 0.6f, 16, Red, 4 * _s, true);
        DrawArc(c, r * 0.86f, -Mathf.Pi / 2 + 0.9f, -Mathf.Pi / 2 + 2.4f, 16, Green, 4 * _s, true);
        DrawArc(c, r * 0.86f, -Mathf.Pi / 2 - 2.4f, -Mathf.Pi / 2 - 0.9f, 16, Green, 4 * _s, true);
        // bateau
        DrawColoredPolygon(new[] { c + new Vector2(0, -r * 0.4f), c + new Vector2(r * 0.14f, r * 0.05f), c + new Vector2(r * 0.1f, r * 0.35f), c + new Vector2(-r * 0.1f, r * 0.35f), c + new Vector2(-r * 0.14f, r * 0.05f) }, Rim);
        float t = (float)angle - Mathf.Pi / 2;
        var d = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
        var tip = c + d * r * 0.5f; var tail = c + d * r * 0.86f;
        DrawLine(tail, tip, Needle, 3.5f * _s, true);
        var side = new Vector2(-d.Y, d.X);
        DrawColoredPolygon(new[] { tip, tip + d * 10 * _s + side * 6 * _s, tip + d * 10 * _s - side * 6 * _s }, Needle);
        Text(c + new Vector2(0, r * 0.55f), $"{knots:N0} {Loc.T("nœuds")}", 13, Tick);
        Text(c + new Vector2(0, r * 0.75f), $"{Loc.T("voiles")} {sails * 100:N0} %", 11, Dim);
    }

    void Bar(Rect2 rect, double v01, string label)
    {
        DrawRect(rect, FaceCol);
        float f = Mathf.Clamp((float)v01, 0, 1);
        DrawRect(new Rect2(rect.Position.X, rect.End.Y - rect.Size.Y * f, rect.Size.X, rect.Size.Y * f), Needle);
        DrawRect(rect, Rim, false, 1.5f * _s);
        Text(new Vector2(rect.GetCenter().X, rect.End.Y + 9 * _s), label, 10, Dim);
    }
}
