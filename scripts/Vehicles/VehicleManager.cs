using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Véhicules du monde : menu « Véhicules » (touche G), apparition devant le joueur, monter / descendre (E),
/// véhicules garés (replacés par rapport à la caméra, obstacles pour le personnage), messages (crash, échouage…).
/// Le vent réel de la météo GFS est branché ici sur <see cref="Vehicle.Wind"/>.
/// </summary>
public partial class VehicleManager : CanvasLayer
{
    public FlyCamera Camera;
    public Weather Weather;
    public Atmosphere Atmosphere;

    const int MaxVehicles = 8;
    readonly List<Vehicle> _vehicles = new();
    readonly Dictionary<Vehicle, Vec3d> _registered = new(); // position à laquelle l'obstacle du véhicule garé a été enregistré
    Node3D _world;
    PanelContainer _panel;
    Label _message;
    double _messageTime;
    // Vent : un échantillon GFS gardé quelques secondes (il varie lentement)
    WeatherSample _windSample;
    Vec3d _windAt = new(0, 0, 0);
    double _windAge = 1e9;

    public bool IsOpen => _panel.Visible;
    public IReadOnlyList<Vehicle> All => _vehicles;

    public override void _Ready()
    {
        Layer = 2;
        ProcessPriority = 100; // après la caméra : les véhicules garés sont placés par rapport à sa position de l'image
        _world = new Node3D { Name = "VehicleNodes" };
        GetParent().CallDeferred(Node.MethodName.AddChild, _world);

        Vehicle.Wind = WindAt;

        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.93f), ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomRight = 8 });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);
        box.AddChild(new Label { Text = "Véhicules — il apparaît devant toi et tu montes dedans (E pour descendre)", HorizontalAlignment = HorizontalAlignment.Center });
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);
        AddButton(row, "1  Voiture", VehicleKind.Car);
        AddButton(row, "2  Vélo", VehicleKind.Bike);
        AddButton(row, "3  Bateau", VehicleKind.Boat);
        AddButton(row, "4  Avion", VehicleKind.Plane);
        box.AddChild(new Label { Text = "Le bateau va sur l'eau la plus proche. L'avion apparaît en vol si tu voles (caméra libre), sinon au sol.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.7f) });

        _message = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
        _message.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.KeepSize, 90);
        _message.GrowHorizontal = Control.GrowDirection.Both;
        _message.GrowVertical = Control.GrowDirection.Begin;
        _message.AddThemeFontSizeOverride("font_size", 22);
        _message.AddThemeColorOverride("font_color", Colors.White);
        _message.AddThemeColorOverride("font_outline_color", Colors.Black);
        _message.AddThemeConstantOverride("outline_size", 5);
        AddChild(_message);
    }

    void AddButton(HBoxContainer row, string text, VehicleKind kind)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(130, 56), FocusMode = Control.FocusModeEnum.None };
        b.Pressed += () => { Toggle(); Spawn(kind); };
        row.AddChild(b);
    }

    public void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        Camera.InputBlocked = _panel.Visible;
        if (_panel.Visible) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Message(string text, double seconds = 4)
    {
        GD.Print($"[Véhicules] {text}");
        _message.Text = text;
        _message.Visible = true;
        _messageTime = seconds;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (_panel.Visible)
        {
            int i = k.PhysicalKeycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, _ => -1 };
            if (i >= 0) { Toggle(); Spawn((VehicleKind)i); GetViewport().SetInputAsHandled(); }
            else if (k.PhysicalKeycode is Key.G or Key.Escape) { Toggle(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (Camera.InputBlocked) return;
        if (k.PhysicalKeycode == Key.G) { Toggle(); GetViewport().SetInputAsHandled(); }
        else if (k.PhysicalKeycode == Key.E && (Camera.Walk || Camera.Vehicle != null))
        {
            GetViewport().SetInputAsHandled();
            EnterOrExit();
        }
    }

    void EnterOrExit()
    {
        if (Camera.Vehicle != null)
        {
            if (!Camera.LeaveVehicle()) Message(Camera.Vehicle is Plane p && !p.OnGround ? "Pose-toi d'abord pour descendre de l'avion" : "Arrête-toi pour descendre", 2.5);
            return;
        }
        Vehicle best = null;
        double bestD = double.MaxValue;
        foreach (var v in _vehicles)
        {
            double d = (v.PosD - Camera.Body.PosD).Length();
            if (d < Math.Max(v.Footprint.halfLength, v.Radius) + 2.5 && d < bestD) { best = v; bestD = d; }
        }
        if (best != null) Camera.EnterVehicle(best);
        else Message("Aucun véhicule à côté de toi — G pour en choisir un", 2.5);
    }

    /// <summary>Fait apparaître un véhicule devant le joueur et le fait monter dedans.</summary>
    public Vehicle Spawn(VehicleKind kind)
    {
        Vec3d basePos; double yaw;
        bool flying = false;
        if (Camera.Vehicle != null)
        {
            var cur = Camera.Vehicle;
            if (!Camera.LeaveVehicle()) { Message("Arrête-toi d'abord pour changer de véhicule", 2.5); return null; }
            basePos = cur.ExitPoint; yaw = cur.HeadingYaw;
        }
        else if (Camera.Walk && Camera.Body != null) { basePos = Camera.Body.PosD; yaw = Camera.Body.Yaw; }
        else
        {
            basePos = Camera.PosD; yaw = Camera.Yaw;
            flying = Camera.Altitude - Camera.GroundHeight > 40;
        }
        var up = basePos.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var head = north * Math.Cos(yaw) + east * Math.Sin(yaw);
        var right = head.Cross(up).Normalized();

        Vehicle v = kind switch
        {
            VehicleKind.Car => new GroundVehicle(false) { Name = "Voiture" },
            VehicleKind.Bike => new GroundVehicle(true) { Name = "Vélo" },
            VehicleKind.Boat => new Boat { Name = "Bateau" },
            _ => new Plane { Name = "Avion" },
        };

        if (v is Plane plane && flying)
        {
            plane.PlaceInAir(basePos, head, 55);
        }
        else if (kind == VehicleKind.Boat)
        {
            var water = FindWater(basePos, head, out var boatHead);
            if (water is not Vec3d w)
            {
                v.Free();
                Message("Pas de lac, de rivière ni de mer à moins de 500 m : approche-toi de l'eau", 4);
                return null;
            }
            v.PlaceAt(w, boatHead);
        }
        else
        {
            // Au sol, devant le joueur (à la verticale de la caméra si on vole)
            double ahead = kind switch { VehicleKind.Car => 5, VehicleKind.Bike => 2.5, _ => 12 };
            var p = flying ? up : basePos + head * ahead + right * (kind == VehicleKind.Plane ? 0 : 0.5);
            v.PlaceAt(p, head);
        }

        _world.AddChild(v);
        _vehicles.Add(v);
        // On garde au plus MaxVehicles véhicules : le plus ancien (garé) disparaît
        for (int i = 0; _vehicles.Count > MaxVehicles && i < _vehicles.Count; i++)
        {
            var old = _vehicles[i];
            if (old == Camera.Vehicle || old == v) continue;
            _vehicles.RemoveAt(i--);
            _registered.Remove(old);
            old.QueueFree();
        }
        Camera.EnterVehicle(v);
        Message(kind switch
        {
            VehicleKind.Car => "Voiture : Z accélérer, S freiner / reculer, Q/D tourner, Espace frein à main",
            VehicleKind.Bike => "Vélo : Z pédaler (Maj en danseuse), S freiner, Q/D tourner",
            VehicleKind.Boat => "Bateau : Z avant, S arrière, Q/D gouvernail",
            _ => flying ? "Avion en vol : S cabrer, Z piquer, Q/D roulis, Maj/Ctrl gaz" : "Avion : Maj pour mettre les gaz, S pour décoller vers 100 km/h, Q/D pour diriger au sol",
        }, 6);
        return v;
    }

    /// <summary>Eau la plus proche : d'abord droit devant, puis en cercles jusqu'à 500 m.
    /// Le bateau est tourné vers le large (direction où l'eau continue).</summary>
    static Vec3d? FindWater(Vec3d from, Vec3d head, out Vec3d boatHead)
    {
        var up = from.Normalized();
        boatHead = head;
        var right = head.Cross(up).Normalized();
        for (double d = 4; d <= 500; d += d < 40 ? 2 : 6)
        {
            int dirs = d < 40 ? 1 : 16;
            for (int i = 0; i < dirs; i++)
            {
                double a = i * 2 * Math.PI / dirs;
                var dir = head * Math.Cos(a) + right * Math.Sin(a);
                var p = (from + dir * d).Normalized() * from.Length();
                // il faut de l'eau sous tout le bateau, pas seulement sous un point de la rive
                if (WaterSurface.At(p, out _) && WaterSurface.At(p + dir * 6, out _) && WaterSurface.At(p + dir.Cross(up) * 3, out _) && WaterSurface.At(p - dir.Cross(up) * 3, out _))
                {
                    boatHead = dir;
                    return p + dir * 3;
                }
            }
        }
        return null;
    }

    Vec3d WindAt(Vec3d p, double heightAboveGround)
    {
        if (Weather == null || !Weather.HasData || Atmosphere == null) return new Vec3d(0, 0, 0);
        // nouvel échantillon toutes les 2 s ou tous les 2 km
        if (_windAge > 2 || (p - _windAt).Length() > 2000)
        {
            Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
            _windSample = Weather.Sample(lat, lon, Atmosphere.GameUtc);
            _windAt = p;
            _windAge = 0;
        }
        var (we, wn) = _windSample.WindAt(heightAboveGround);
        Geo.LocalFrame(p.Normalized(), out var east, out var north);
        return east * we + north * wn;
    }

    public override void _Process(double delta)
    {
        _windAge += delta;
        if (_messageTime > 0 && (_messageTime -= delta) <= 0) _message.Visible = false;

        foreach (var v in _vehicles)
        {
            if (v.Notice != null) { Message(v.Notice, 4); v.Notice = null; }
            if (v == Camera.Vehicle)
            {
                if (_registered.Remove(v)) Obstacles.Set(v, null);
                continue;
            }
            double dist = (v.PosD - Camera.PosD).Length();
            v.Visible = dist < 20_000;
            if (dist < 3000) Settle(v, delta);
            v.Place(Camera.PosD, Camera.WorldRotation);

            // Garé : obstacle pour le personnage (sa vraie emprise au sol)
            var (hl, hw, height) = v.Footprint;
            if (hl > 0 && (!_registered.TryGetValue(v, out var at) || (at - v.PosD).Length() > 0.3))
            {
                var f = v.Fwd; var r = v.Right;
                double alt = v.PosD.Length() - Geo.Radius;
                var ring = new[] { v.PosD + f * hl + r * hw, v.PosD + f * hl - r * hw, v.PosD - f * hl - r * hw, v.PosD - f * hl + r * hw };
                Obstacles.Set(v, new[] { Obstacles.Shape.Polygon(ring, alt - 0.3, alt + height) });
                _registered[v] = v.PosD;
            }
        }
    }

    /// <summary>Véhicule garé : le bateau flotte et dérive doucement ; les autres restent posés sur le relief
    /// (qui peut s'affiner quand les données 0,5 m arrivent).</summary>
    static void Settle(Vehicle v, double delta)
    {
        if (v is Boat) { v.Step(new VehicleInput(), delta); return; }
        if (v is Plane p && !p.OnGround) { v.Step(new VehicleInput(), delta); return; } // avion abandonné en vol (téléportation)
        var up = v.PosD.Normalized();
        v.PosD = up * (Geo.Radius + Vehicle.Ground(v.PosD, out _));
    }
}
