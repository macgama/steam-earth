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
    RoadLayer _roads;
    PlacesLayer _places;
    TransitLayer _transit;
    StreetNamesLayer _streets;
    Label _credits;
    CountryMenu _menu;
    string _gotoCountry;
    bool _openMenu;
    string _shotPath;
    static readonly System.Globalization.CultureInfo Fr = new("fr-FR");
    int _frames, _idleFrames, _minFrames = 30;

    public override void _Ready()
    {
        HeightProvider.Init();
        LandCover.Init();
        TimeZones.Init();
        Geography.Init();
        Overture.Init(System.IO.Path.Combine(OS.GetUserDataDir(), "cache", "overture"));

        _camera = new FlyCamera { Name = "Camera" };
        AddChild(_camera);
        // Personnage du mode marche (F), placé chaque image par la caméra
        _camera.Body = new Character { Name = "Character", Visible = false };
        AddChild(_camera.Body);

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
        _roads = new RoadLayer { Name = "Roads", Camera = _camera, Planet = _planet };
        AddChild(_roads);
        _places = new PlacesLayer { Name = "Places", Camera = _camera, Planet = _planet };
        AddChild(_places);
        _transit = new TransitLayer { Name = "Transit", Camera = _camera, Planet = _planet };
        AddChild(_transit);
        _streets = new StreetNamesLayer { Name = "StreetNames", Camera = _camera, Planet = _planet };
        AddChild(_streets);
        _menu = new CountryMenu { Name = "CountryMenu", Camera = _camera };
        AddChild(_menu);

        var layer = new CanvasLayer();
        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
        // Mention obligatoire des données OpenStreetMap (licence ODbL)
        _credits = new Label { Text = Overture.Attribution + ", OurAirports, ESA WorldCover 2021 (CC BY 4.0), Copernicus DEM", HorizontalAlignment = HorizontalAlignment.Right };
        _credits.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 10);
        _credits.GrowHorizontal = Control.GrowDirection.Begin;
        _credits.GrowVertical = Control.GrowDirection.Begin;
        _credits.AddThemeFontSizeOverride("font_size", 13);
        _credits.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.8f));
        _credits.AddThemeColorOverride("font_outline_color", Colors.Black);
        _credits.AddThemeConstantOverride("outline_size", 3);
        layer.AddChild(_credits);
        AddChild(layer);

        int view = 1;
        bool walk = false;
        double? pitch = null;
        double[] at = null;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--view=")) view = int.Parse(a.Substring(7));
            if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);
            if (a == "--walk") walk = true;
            if (a == "--fp") _camera.ThirdPerson = false;           // marche à la première personne
            if (a.StartsWith("--pitch=")) pitch = double.Parse(a.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
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
        if (pitch is double pd) _camera.Pitch = pd * Math.PI / 180; // inclinaison de la caméra (tests)
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
        var player = _camera.PlayerPos; // le personnage en mode marche, sinon la caméra
        double alt = player.Length() - Geo.Radius;
        double ground = _camera.Walk && _camera.Body != null ? _camera.Body.GroundHeight : _camera.GroundHeight;
        var w = _atmosphere.Current;
        var utc = _atmosphere.GameUtc;
        Geo.ToLatLon(_camera.PosD.Normalized(), out double camLat, out double camLon);
        double solarH = ((utc.TimeOfDay.TotalHours + camLon / 15) % 24 + 24) % 24;
        var local = TimeZones.LocalTime(utc, camLat, camLon, out string zone, out TimeSpan offset, out bool dst);
        string precip = w.PrecipMmH < 0.1 ? "pas de précipitations" : $"{(w.FrozenFraction > 0.5 || w.TemperatureC < 0.5 ? "neige" : "pluie")} {w.PrecipMmH:F1} mm/h";

        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);
        _hud.Text =
            $"Lat {lat:F4}°  Lon {lon:F4}°\n" +
            $"Altitude {alt:N0} m   Sol {ground:N0} m   Hauteur {alt - ground:N0} m\n" +
            $"Vitesse {_camera.Speed * 3.6:N0} km/h  ×{_camera.SpeedMultiplier:F2}  {(_camera.Walk ? $"[MARCHE{(_camera.Grounded ? "" : ", en l'air")}, {(_camera.ThirdPerson ? "vue arrière" : "1re personne")}]" : "")}\n" +
            $"Chunks {_planet.VisibleChunks} visibles / {_planet.TotalChunks} (niveau max {_planet.DeepestLevel})   en construction {_planet.PendingBuilds}   relief : {(StreamedRaster.Downloading > 0 ? $"téléchargement ({StreamedRaster.Downloading})" : "à jour")}{(SwissAlti3D.Covers(lat, lon) ? "   zone HD 0,5 m (swissALTI3D © swisstopo)" : "")}   arbres {Vegetation.Instances:N0}\n" +
            $"Heure locale {local:HH:mm} ({local.ToString("dddd d MMMM", Fr)}, {zone}, {TimeZones.FormatOffset(offset)}{(dst ? ", heure d'été" : "")})   {utc:HH:mm} UTC{(_atmosphere.TimeOffsetHours != 0 ? $" (décalage {_atmosphere.TimeOffsetHours:+0.##;-0.##} h)" : "")}   heure solaire {(int)solarH:00}:{(int)(solarH % 1 * 60):00}   soleil {_atmosphere.SunElevationDeg:F0}° {(_atmosphere.SunElevationDeg > 0 ? "(jour)" : _atmosphere.SunElevationDeg > -6 ? "(crépuscule)" : "(nuit)")}\n" +
            $"Météo ({_weather.Status}) : {w.TemperatureC:F1} °C   vent {w.Wind10Speed * 3.6:F0} km/h du {w.Wind10FromDeg:F0}° (rafales {w.Gust * 3.6:F0})   nuages bas/moy/hauts {w.CloudLow * 100:F0}/{w.CloudMid * 100:F0}/{w.CloudHigh * 100:F0} %   {precip}   visibilité {w.VisibilityM / 1000:F1} km\n" +
            $"Routes et rails : {_roads.Status}{(_roads.ShowLayer ? "" : " (masqués)")}   noms de lieux : {_places.Count}{(_places.ShowLayer ? "" : " (masqués)")}   rues : {_streets.Count}   arrêts et gares : {_transit.Count - _transit.Airports}, aéroports : {_transit.Airports}{(_transit.ShowLayer ? "" : " (masqués)")}   {(PmTilesArchive.Downloading > 0 ? $"téléchargement OSM ({PmTilesArchive.Downloading})" : "")}\n" +
            $"FPS {Engine.GetFramesPerSecond()}\n" +
            "Clic = souris  ZQSD/WASD  Espace/E monter  C/A descendre  Maj ×8  Molette vitesse  F marcher (Espace saute, Maj court, V vue arrière/1re personne, molette distance)  1-9 lieux  +/− heure  R heure réelle  Tab pays  B frontières  T routes/rails  N noms des lieux et des rues  P transports (arrêts, gares, aéroports, ports)";

        if (_shotPath != null)
        {
            _idleFrames = _planet.Idle && _political.Ready && _roads.Idle && _places.Idle && _transit.Idle && _streets.Idle && StreamedRaster.Downloading == 0 && PmTilesArchive.Downloading == 0 ? _idleFrames + 1 : 0;
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
