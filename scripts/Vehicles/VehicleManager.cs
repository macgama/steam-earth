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

    const int MaxVehicles = 10;
    readonly List<Vehicle> _vehicles = new();
    readonly Dictionary<Vehicle, Vec3d> _registered = new(); // position à laquelle l'obstacle du véhicule garé a été enregistré
    Node3D _world;
    PanelContainer _panel;
    readonly List<Button> _buttons = new();
    VehicleAudio _audio;
    VehicleDashboard _dashboard;
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
        _world = new Node3D { Name = "VehicleNodes", ProcessMode = ProcessModeEnum.Pausable }; // arrêtés pendant la pause
        GetParent().CallDeferred(Node.MethodName.AddChild, _world);

        Vehicle.Wind = WindAt;
        _audio = new VehicleAudio { Name = "VehicleAudio" };
        AddChild(_audio);
        _dashboard = new VehicleDashboard { Name = "VehicleDashboard" };
        AddChild(_dashboard);
        GraphicsQuality.Changed += (_, _) => { foreach (var v in _vehicles) ApplyQuality(v); };

        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.93f), ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomRight = 8 });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);
        box.AddChild(new Label { Text = "Véhicules — il apparaît devant toi et tu montes dedans (E pour descendre)", HorizontalAlignment = HorizontalAlignment.Center });
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 14);
        box.AddChild(cols);
        foreach (var cat in VehicleCatalog.Categories)
        {
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            cols.AddChild(col);
            col.AddChild(new Label { Text = cat, HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 0.85f, 0.5f) });
            foreach (var spec in VehicleCatalog.All)
                if (spec.Category == cat) AddButton(col, spec);
        }
        box.AddChild(new Label { Text = "Flèches + Entrée pour choisir, G ou Échap pour fermer. Les bateaux vont sur l'eau la plus proche ; les avions et l'hélicoptère apparaissent en vol si tu voles (caméra libre), sinon au sol.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(620, 0) });

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

    void AddButton(VBoxContainer col, VehicleSpec spec)
    {
        var b = new Button { Text = spec.Name, CustomMinimumSize = new Vector2(170, 38), FocusMode = Control.FocusModeEnum.All };
        b.Pressed += () => { Toggle(); Spawn(spec); };
        col.AddChild(b);
        _buttons.Add(b);
    }

    public void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        Camera.InputBlocked = _panel.Visible;
        if (_panel.Visible) { Input.MouseMode = Input.MouseModeEnum.Visible; if (_buttons.Count > 0) _buttons[0].GrabFocus(); }
        else GetViewport().GuiReleaseFocus();
    }

    public void Message(string text, double seconds = 4)
    {
        text = Loc.T(text);
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
            if (k.PhysicalKeycode is Key.G or Key.Escape) { Toggle(); GetViewport().SetInputAsHandled(); }
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
            if (!Camera.LeaveVehicle()) Message(Camera.Vehicle.Spec.InAir && !Camera.Vehicle.Grounded ? "Pose-toi d'abord pour descendre" : "Arrête-toi pour descendre", 2.5);
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
    public Vehicle Spawn(VehicleSpec spec)
    {
        if (Camera.Ride != null) { Message("Descends d'abord du train ou du bus (H)", 2.5); return null; }
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

        var v = spec.Create();
        if (flying && v is Plane plane) plane.PlaceInAir(basePos, head, spec.StallSpeed * 1.7);
        else if (flying && v is Helicopter heli) heli.PlaceInAir(basePos, head);
        else if (spec.OnWater)
        {
            var water = FindWater(basePos, head, spec.Length, out var boatHead);
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
            double ahead = spec.Length / 2 + (spec.InAir ? 6 : 2.5);
            var p = flying ? up : basePos + head * ahead + right * (spec.InAir ? 0 : 0.5);
            v.PlaceAt(p, head);
        }

        _world.AddChild(v);
        ApplyQuality(v);
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
        // rappel des touches (celui du tableau de bord)
        string hud = v.Hud();
        int k = hud.IndexOf("   [", StringComparison.Ordinal);
        string keys = k >= 0 ? hud[(k + 4)..].TrimEnd(']') : "";
        string tip = spec.Family switch
        {
            VehicleFamily.Plane or VehicleFamily.Jet when !flying => " — " + Loc.T("Maj pour mettre les gaz, puis S pour décoller"),
            VehicleFamily.Helicopter when !flying => " — " + Loc.T("Maj pour lancer le rotor et monter"),
            _ => "",
        };
        Message(Loc.T(spec.Name) + " : " + keys + tip, 7);
        return v;
    }

    /// <summary>Test / captures : gare les véhicules <paramref name="ids"/> en rang, à 18 m devant la caméra.</summary>
    public void Showroom(string[] ids)
    {
        var up = Camera.PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var head = north * Math.Cos(Camera.Yaw) + east * Math.Sin(Camera.Yaw);
        var right = head.Cross(up).Normalized();
        double width = 0;
        var specs = new List<VehicleSpec>();
        foreach (var id in ids) { var s = VehicleCatalog.Get(id); specs.Add(s); width += Math.Min(s.Width, 12) + 1.5; }
        double x = -width / 2;
        foreach (var s in specs)
        {
            double w = Math.Min(s.Width, 12) + 1.5;
            var v = s.Create();
            // de trois quarts : on voit l'avant et le flanc
            var dir = (head * -0.7 + right * 0.7).Normalized();
            var p = Camera.PosD + head * 18 + right * (x + w / 2);
            if (s.OnWater) { var wp = FindWater(p, head, s.Length, out _); if (wp is Vec3d ww) p = ww; }
            v.PlaceAt(p, dir);
            _world.AddChild(v);
            ApplyQuality(v);
            _vehicles.Add(v);
            v.Place(Camera.PosD, Camera.WorldRotation);
            x += w;
        }
    }

    /// <summary>Compatibilité : véhicule par identifiant du catalogue (car, bike, boat, plane, heli…).</summary>
    public Vehicle Spawn(string id) => Spawn(VehicleCatalog.Get(id));

    /// <summary>Eau la plus proche : d'abord droit devant, puis en cercles jusqu'à 500 m.
    /// Le bateau est tourné vers le large (direction où l'eau continue).</summary>
    static Vec3d? FindWater(Vec3d from, Vec3d head, double length, out Vec3d boatHead)
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
                if (WaterSurface.At(p, out _) && WaterSurface.At(p + dir * length, out _) && WaterSurface.At(p + dir.Cross(up) * 3, out _) && WaterSurface.At(p - dir.Cross(up) * 3, out _))
                {
                    boatHead = dir;
                    return p + dir * (length / 2);
                }
            }
        }
        return null;
    }

    /// <summary>Ombres des véhicules selon le niveau graphique (Bas : aucune).</summary>
    static void ApplyQuality(Node n)
    {
        if (n is GeometryInstance3D g) g.CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Vehicles);
        foreach (var c in n.GetChildren()) ApplyQuality(c);
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
        _audio.Update(Camera.Vehicle, delta);
        _dashboard.Vehicle = Camera.Vehicle != null && !Camera.ThirdPerson ? Camera.Vehicle : null;
        _message.OffsetBottom = _dashboard.Vehicle != null ? -280 : -90; // au-dessus du tableau de bord
        _message.OffsetTop = _message.OffsetBottom;
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
                Obstacles.Set(v, new[] { Obstacles.Shape.Polygon(ring, null, alt - 0.3, alt + height) });
                _registered[v] = v.PosD;
            }
        }
    }

    /// <summary>Véhicule garé : le bateau flotte et dérive doucement ; les autres restent posés sur le relief
    /// (qui peut s'affiner quand les données 0,5 m arrivent).</summary>
    static void Settle(Vehicle v, double delta)
    {
        if (v.Spec.OnWater) { v.Step(new VehicleInput(), delta); return; }
        if (v.Spec.InAir && !v.Grounded) { v.Step(new VehicleInput(), delta); return; } // abandonné en vol (téléportation)
        var up = v.PosD.Normalized();
        v.PosD = up * (Geo.Radius + Vehicle.Ground(v.PosD, out _));
    }
}
