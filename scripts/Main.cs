using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Scène principale : construit la planète, la caméra, le soleil, le ciel et l'affichage d'infos.
/// Arguments (après « -- ») pour les tests automatiques :
///   --view=N        lieu de départ (1 à 5, comme les touches)
///   --shot=fichier  capture d'écran une fois le terrain chargé, puis quitte
/// </summary>
public partial class Main : Node3D
{
    Planet _planet;
    FlyCamera _camera;
    Label _hud;
    WorldEnvironment _env;
    Godot.Environment _environment;
    DirectionalLight3D _sun;
    string _shotPath;
    int _frames, _idleFrames;

    public override void _Ready()
    {
        HeightProvider.Init();

        _camera = new FlyCamera { Name = "Camera" };
        AddChild(_camera);

        _planet = new Planet { Name = "Planet", Camera = _camera };
        AddChild(_planet);

        // Soleil : direction fixe dans le repère planète (au-dessus de ~lat 25°N, lon 30°E)
        var sunDir = Geo.FromLatLon(25, 30).ToVector3();
        _sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.6f, ShadowEnabled = false };
        _sun.Transform = new Transform3D(Basis.LookingAt(-sunDir, Vector3.Up), Vector3.Zero);
        _planet.AddChild(_sun); // enfant de la planète : tourne avec le repère planète

        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() };
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogLightColor = new Color(0.62f, 0.72f, 0.85f),
            FogDensity = 0.00003f,
            FogSkyAffect = 0.0f,
        };
        _env = new WorldEnvironment { Environment = _environment };
        AddChild(_env);

        var layer = new CanvasLayer();
        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
        AddChild(layer);

        int view = 1;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--view=")) view = int.Parse(a.Substring(7));
            if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);
        }
        var key = new InputEventKey { PhysicalKeycode = Key.Key1 + (view - 1), Pressed = true };
        _camera._UnhandledInput(key);
    }

    public override void _Process(double delta)
    {
        _frames++;
        double alt = _camera.Altitude;
        // Au-delà de ~60 km : ciel noir (espace), pas de brouillard
        bool space = alt > 60_000;
        _environment.BackgroundMode = space ? Godot.Environment.BGMode.Color : Godot.Environment.BGMode.Sky;
        _environment.BackgroundColor = Colors.Black;
        _environment.FogEnabled = !space;
        _environment.FogDensity = (float)(0.00003 * Math.Exp(-Math.Max(0, alt) / 8000));

        Geo.ToLatLon(_camera.PosD.Normalized(), out double lat, out double lon);
        _hud.Text =
            $"Lat {lat:F4}°  Lon {lon:F4}°\n" +
            $"Altitude {alt:N0} m   Sol {_camera.GroundHeight:N0} m   Hauteur {alt - _camera.GroundHeight:N0} m\n" +
            $"Vitesse {_camera.Speed * 3.6:N0} km/h  ×{_camera.SpeedMultiplier:F2}  {(_camera.Walk ? "[MARCHE]" : "")}\n" +
            $"Chunks {_planet.VisibleChunks} visibles / {_planet.TotalChunks}   en construction {_planet.PendingBuilds}   tuiles 30 m chargées {HeightProvider.LoadedTiles}/{HeightProvider.AvailableTiles}\n" +
            $"FPS {Engine.GetFramesPerSecond()}\n" +
            "Clic = souris  ZQSD/WASD  Espace/E monter  C/A descendre  Maj ×8  Molette vitesse  F marcher  1-5 lieux";

        if (_shotPath != null)
        {
            _idleFrames = _planet.Idle ? _idleFrames + 1 : 0;
            if ((_frames > 30 && _idleFrames > 20) || _frames > 4000)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(_shotPath);
                GD.Print($"[Main] capture enregistrée : {_shotPath} (après {_frames} images)");
                GetTree().Quit();
            }
        }
    }
}
