using System;
using Godot;
using SteamEarth.Data;

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
    Weather _weather;
    Atmosphere _atmosphere;
    PoliticalLayer _political;
    CountryMenu _menu;
    string _gotoCountry;
    bool _openMenu;
    string _shotPath;
    static readonly System.Globalization.CultureInfo Fr = new("fr-FR");
    int _frames, _idleFrames, _minFrames = 30;

    public override void _Ready()
    {
        HeightProvider.Init();
        TimeZones.Init();
        Geography.Init();

        _camera = new FlyCamera { Name = "Camera" };
        AddChild(_camera);

        _planet = new Planet { Name = "Planet", Camera = _camera };
        AddChild(_planet);

        // Soleil : enfant de la planète (sa direction est exprimée dans le repère planète),
        // orienté à chaque image par Atmosphere selon l'heure UTC réelle.
        _sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.6f, ShadowEnabled = false };
        _planet.AddChild(_sun);

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

        _weather = new Weather { Name = "Weather" };
        AddChild(_weather);
        _atmosphere = new Atmosphere { Name = "Atmosphere", Camera = _camera, Planet = _planet, Weather = _weather, Sun = _sun, Env = _environment };
        AddChild(_atmosphere);
        _political = new PoliticalLayer { Name = "Political", Camera = _camera, Planet = _planet };
        AddChild(_political);
        _menu = new CountryMenu { Name = "CountryMenu", Camera = _camera };
        AddChild(_menu);

        var layer = new CanvasLayer();
        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
        AddChild(layer);

        int view = 1;
        bool walk = false;
        double[] at = null;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--view=")) view = int.Parse(a.Substring(7));
            if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);
            if (a == "--walk") walk = true;
            if (a.StartsWith("--country=")) _gotoCountry = a.Substring(10);
            if (a == "--menu") _openMenu = true;
            if (a == "--autowalk") { walk = true; _camera.AutoForward = true; _minFrames = 600; }
            if (a.StartsWith("--at="))
            {
                var p = Array.ConvertAll(a.Substring(5).Split(','), x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
                at = p;
            }
            if (a.StartsWith("--hours=")) _atmosphere.TimeOffsetHours = double.Parse(a.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
        }
        var key = new InputEventKey { PhysicalKeycode = Key.Key1 + (view - 1), Pressed = true };
        _camera._UnhandledInput(key);
        if (at != null) _camera.Teleport(at[0], at[1], at[2], at[3], at[4]);
        if (walk) _camera.SetWalk(true);
        if (_gotoCountry != null)
        {
            var c = Geography.Countries.Find(x => x.Name == _gotoCountry);
            if (c != null) _menu.GoTo(c);
        }
        if (_openMenu) _menu.CallDeferred(CountryMenu.MethodName.Toggle);
    }

    public override void _Process(double delta)
    {
        _frames++;
        double alt = _camera.Altitude;
        var w = _atmosphere.Current;
        var utc = _atmosphere.GameUtc;
        Geo.ToLatLon(_camera.PosD.Normalized(), out double camLat, out double camLon);
        double solarH = ((utc.TimeOfDay.TotalHours + camLon / 15) % 24 + 24) % 24;
        var local = TimeZones.LocalTime(utc, camLat, camLon, out string zone, out TimeSpan offset, out bool dst);
        string precip = w.PrecipMmH < 0.1 ? "pas de précipitations" : $"{(w.FrozenFraction > 0.5 || w.TemperatureC < 0.5 ? "neige" : "pluie")} {w.PrecipMmH:F1} mm/h";

        Geo.ToLatLon(_camera.PosD.Normalized(), out double lat, out double lon);
        _hud.Text =
            $"Lat {lat:F4}°  Lon {lon:F4}°\n" +
            $"Altitude {alt:N0} m   Sol {_camera.GroundHeight:N0} m   Hauteur {alt - _camera.GroundHeight:N0} m\n" +
            $"Vitesse {_camera.Speed * 3.6:N0} km/h  ×{_camera.SpeedMultiplier:F2}  {(_camera.Walk ? (_camera.Grounded ? "[MARCHE]" : "[MARCHE, en l'air]") : "")}\n" +
            $"Chunks {_planet.VisibleChunks} visibles / {_planet.TotalChunks} (niveau max {_planet.DeepestLevel})   en construction {_planet.PendingBuilds}   relief 30 m : {(StreamedRaster.Downloading > 0 ? $"téléchargement ({StreamedRaster.Downloading})" : "à jour")}\n" +
            $"Heure locale {local:HH:mm} ({local.ToString("dddd d MMMM", Fr)}, {zone}, {TimeZones.FormatOffset(offset)}{(dst ? ", heure d'été" : "")})   {utc:HH:mm} UTC{(_atmosphere.TimeOffsetHours != 0 ? $" (décalage {_atmosphere.TimeOffsetHours:+0.##;-0.##} h)" : "")}   heure solaire {(int)solarH:00}:{(int)(solarH % 1 * 60):00}   soleil {_atmosphere.SunElevationDeg:F0}° {(_atmosphere.SunElevationDeg > 0 ? "(jour)" : _atmosphere.SunElevationDeg > -6 ? "(crépuscule)" : "(nuit)")}\n" +
            $"Météo ({_weather.Status}) : {w.TemperatureC:F1} °C   vent {w.Wind10Speed * 3.6:F0} km/h du {w.Wind10FromDeg:F0}° (rafales {w.Gust * 3.6:F0})   nuages bas/moy/hauts {w.CloudLow * 100:F0}/{w.CloudMid * 100:F0}/{w.CloudHigh * 100:F0} %   {precip}   visibilité {w.VisibilityM / 1000:F1} km\n" +
            $"FPS {Engine.GetFramesPerSecond()}\n" +
            "Clic = souris  ZQSD/WASD  Espace/E monter  C/A descendre  Maj ×8  Molette vitesse  F marcher (Espace saute, Maj court)  1-9 lieux  +/− heure  R heure réelle  Tab pays  B frontières";

        if (_shotPath != null)
        {
            _idleFrames = _planet.Idle && _political.Ready && StreamedRaster.Downloading == 0 ? _idleFrames + 1 : 0;
            if ((_frames > _minFrames && _idleFrames > 20) || _frames > 4000)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(_shotPath);
                GD.Print($"[Main] capture enregistrée : {_shotPath} (après {_frames} images)");
                GetTree().Quit();
            }
        }
    }
}
