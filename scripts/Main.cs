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
    GameHud _hud;
    GameUi _ui;
    WorldEnvironment _env;
    Godot.Environment _environment;
    DirectionalLight3D _sun;
    Weather _weather;
    GroundCover _ground;
    Atmosphere _atmosphere;
    Waves _waves;
    Ocean _ocean;
    PoliticalLayer _political;
    RoadLayer _roads;
    PlacesLayer _places;
    BuildingsLayer _buildings;
    BuildingInteriors _interiors;
    string _enterTest; int _enterStage; int _enterFrame; bool _stairsTest;
    StartMenu _start;
    VehicleManager _vehicles;
    string _spawnVehicle;
    GraphicsMenu _graphicsMenu;
    TransitLayer _transit;
    StreetNamesLayer _streets;
    TransitRide _ride;
    string _rideTest;
    CountryMenu _menu;
    string _gotoCountry;
    bool _openMenu;
    string _shotPath;
    static readonly System.Globalization.CultureInfo Fr = new("fr-FR");
    int _frames, _idleFrames, _minFrames = 30;
    double? _testPitch; // --pitch : gardé quand le personnage est posé (tests à la première personne)
    int _walkTest; // > 0 : nombre d'images de marche automatique restant à faire après le chargement

    public override void _Ready()
    {
        GraphicsQuality.Init(); // niveau Bas/Moyen/Élevé/Ultra (détecté au premier lancement), avant les couches
        UiSettings.Init(GetTree().Root); // langue, taille de l'interface, plein écran, souris
        GetTree().Root.Theme = UiTheme.Get(); // style commun à tous les menus
        // Pause (Échap) : seuls le joueur, les véhicules et les voyages s'arrêtent ; le monde continue de se charger
        ProcessMode = ProcessModeEnum.Always;
        HeightProvider.Init();
        LandCover.Init();
        TimeZones.Init();
        Geography.Init();
        NightLights.Init();
        Overture.Init(System.IO.Path.Combine(OS.GetUserDataDir(), "cache", "overture"));

        _camera = new FlyCamera { Name = "Camera", ProcessMode = ProcessModeEnum.Pausable };
        AddChild(_camera);
        // Personnage du mode marche (F), placé chaque image par la caméra
        _camera.Body = new Character { Name = "Character", Visible = false, ProcessMode = ProcessModeEnum.Pausable };
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
        // Herbe, rochers et cailloux autour du joueur (enfant de la planète : même repère que le terrain)
        _ground = new GroundCover { Name = "GroundCover", Camera = _camera, Atmosphere = _atmosphere, Planet = _planet };
        _planet.AddChild(_ground);
        AddChild(_atmosphere);
        // Océans : houle réelle (NOAA GFS-Wave) et fonds marins (GEBCO) ; après Atmosphere (brouillard sous l'eau)
        _waves = new Waves { Name = "Waves" };
        AddChild(_waves);
        _ocean = new Ocean { Name = "Ocean", Camera = _camera, Waves = _waves, Atmosphere = _atmosphere, Env = _environment };
        AddChild(_ocean);
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
        _ride = new TransitRide { Name = "TransitRide", Camera = _camera, Planet = _planet, Atmosphere = _atmosphere, ProcessMode = ProcessModeEnum.Pausable };
        AddChild(_ride);
        _menu = new CountryMenu { Name = "CountryMenu", Camera = _camera };
        AddChild(_menu);
        _buildings = new BuildingsLayer { Name = "Buildings", Camera = _camera, Planet = _planet, Places = _places, Political = _political, Menu = _menu };
        AddChild(_buildings);
        _interiors = new BuildingInteriors { Name = "Interiors", Camera = _camera, Planet = _planet, Buildings = _buildings };
        AddChild(_interiors);
        AddChild(new GraphicsApplier { Name = "GraphicsApplier", Env = _environment, Sun = _sun });
        _graphicsMenu = new GraphicsMenu { Name = "GraphicsMenu", Camera = _camera };
        AddChild(_graphicsMenu);
        _start = new StartMenu { Name = "StartMenu", Camera = _camera, Buildings = _buildings };
        AddChild(_start);
        _buildings.Start = _start;
        _vehicles = new VehicleManager { Name = "Vehicles", Camera = _camera, Weather = _weather, Atmosphere = _atmosphere, ProcessMode = ProcessModeEnum.Pausable };
        AddChild(_vehicles);
        _menu.Arrived += c => _buildings.Build(c.Capital, c.Lat, c.Lon, BuildingsLayer.RadiusFor("capital", 0), BuildingsLayer.MaxRadiusFor("capital", 0));

        _menu.ShowButton = false; // Tab, ou le menu pause

        // Interface : HUD discret, infos techniques (F3), écran titre, pause, options, commandes, crédits
        _hud = new GameHud { Name = "Hud", Camera = _camera, Atmosphere = _atmosphere, Weather = _weather, DebugText = DebugText };
        AddChild(_hud);
        _ui = new GameUi { Name = "GameUi", Camera = _camera, Start = _start, Graphics = _graphicsMenu, Countries = _menu, Vehicles = _vehicles, Ride = _ride, Hud = _hud };
        AddChild(_ui);
        UiTheme.ApplyTo(this); // même style pour tous les menus (départ, pays, véhicules, graphismes…)

        int view = 1;
        // Écran titre au lancement, sauf pour les tests automatiques qui donnent déjà un lieu
        bool startMenu = true, directStart = false;
        bool walk = false;
        double? pitch = null;
        double[] at = null;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--view=") || a.StartsWith("--at=") || a.StartsWith("--country=") || a.StartsWith("--build=") || a == "--walk" || a == "--autowalk" || a == "--menu" || a == "--nostart") startMenu = false;
            if (a == "--start") { startMenu = true; directStart = true; } // menu de départ directement (tests)
            if (a is "--pause" or "--controls" or "--options" or "--credits") startMenu = false;
            if (a.StartsWith("--view=")) view = int.Parse(a.Substring(7));
            if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);
            if (a == "--walk") walk = true;
            // --vehicle=car|bike|boat|plane : fait apparaître un véhicule et monte dedans (tests)
            if (a.StartsWith("--vehicle=")) { _spawnVehicle = a.Substring(10); startMenu = false; }
            if (a.StartsWith("--ride=")) { _rideTest = a.Substring(7); _minFrames = 300; } // N,minutes après le départ
            if (a == "--board") _rideTest = "board";
            if (a == "--fp") _camera.ThirdPerson = false;           // marche à la première personne
            if (a.StartsWith("--pitch=")) pitch = double.Parse(a.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
            if (a.StartsWith("--country=")) _gotoCountry = a.Substring(10);
            if (a == "--menu") _openMenu = true;
            if (a == "--autowalk") { walk = true; _camera.AutoForward = true; _minFrames = 600; }
            // --walktest=N : attend que le monde soit chargé, puis avance seul N images (test des collisions)
            // (le personnage n'apparaît qu'une fois le monde chargé) ; --climb : en tenant Espace
            if (a.StartsWith("--walktest=")) _walkTest = int.Parse(a.Substring(11));
            if (a == "--climb") _camera.AutoJump = true;
            // --enter=church|school|shop|tall|any : une fois la ville construite, devant la porte d'un tel bâtiment ;
            // --stairs : puis au pied de l'escalier (avec --walktest=N, il monte tout seul)
            if (a.StartsWith("--enter=")) { _enterTest = a.Substring(8); walk = true; }
            if (a == "--stairs") _stairsTest = true;
            if (a == "--clear") _atmosphere.ClearSky = true; // tests : sans nuages
            if (a.StartsWith("--at="))
            {
                var p = Array.ConvertAll(a.Substring(5).Split(','), x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
                at = p;
            }
            if (a.StartsWith("--build="))
            {
                var p = Array.ConvertAll(a.Substring(8).Split(','), x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
                _buildings.Build("test", p[0], p[1], p.Length > 2 ? p[2] : 1500, p.Length > 3 ? p[3] : 0);
            }
            if (a.StartsWith("--hours=")) _atmosphere.TimeOffsetHours = double.Parse(a.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
        }
        var key = new InputEventKey { PhysicalKeycode = Key.Key1 + (view - 1), Pressed = true };
        _camera._UnhandledInput(key);
        if (at != null) _camera.Teleport(at[0], at[1], at[2], at[3], at[4]);
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--fly") >= 0) walk = false; // tests de véhicules : rester en vol libre (avion en vol)
        if (walk) _camera.SetWalk(true);
        if (pitch is double pd) _camera.Pitch = pd * Math.PI / 180; // inclinaison de la caméra (tests)
        _testPitch = pitch;
        if (_gotoCountry != null)
        {
            var c = Geography.Countries.Find(x => x.Name == _gotoCountry);
            if (c != null) _menu.GoTo(c);
        }
        if (_openMenu) _menu.CallDeferred(CountryMenu.MethodName.Toggle);
        if (directStart) _start.CallDeferred(StartMenu.MethodName.Open, true);
        else if (startMenu) _ui.CallDeferred(GameUi.MethodName.ShowTitle);
        if (_rideTest == "board") StartBoardTest();
        else if (_rideTest != null)
        {
            var r = _rideTest.Split(',');
            _ride.TestRide(r[0], r.Length > 1 ? double.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture) : 0);
        }
    }

    /// <summary>Test --enter : vrai tant que le personnage n'est pas encore placé devant la porte (ou dans l'escalier).</summary>
    bool EnterTest()
    {
        bool loaded = _planet.Idle && _buildings.Idle && StreamedRaster.Downloading == 0 && PmTilesArchive.Downloading == 0 && _frames > 60;
        switch (_enterStage)
        {
            case 0:
                if (_frames % 200 == 0) GD.Print($"[Test] attente : relief {_planet.Idle} bâtiments {_buildings.Idle} ({_buildings.Status}) téléchargements {StreamedRaster.Downloading}/{PmTilesArchive.Downloading}, image {_frames}");
                if (!loaded && _frames < 1200) return true;
                var where = _interiors.TestDoor(_enterTest);
                GD.Print($"[Test] devant la porte : {where ?? "aucun bâtiment trouvé"}");
                if (where == null) { _enterTest = null; return false; }
                _enterStage = 1; _enterFrame = _frames;
                return true;
            case 1:
                if (_frames - _enterFrame < 30 || !_interiors.Idle || !_interiors.HasInteriorNear(10)) return _frames - _enterFrame < 900;
                if (_stairsTest) GD.Print($"[Test] {_interiors.TestStairs() ?? "pas d'escalier"}");
                _enterStage = 2; _enterFrame = _frames;
                return true;
            case 2:
                if (_frames - _enterFrame < 20) return true;
                _enterStage = 3;
                if (_walkTest > 0) _camera.AutoForward = true; // marche tout de suite (le relief lointain peut encore charger)
                return false;
            default:
                return false;
        }
    }

    async void StartBoardTest()
    {
        while (!_ride.Loaded) await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        _ride.OpenBoard();
    }

    void SpawnTestVehicle()
    {
        var kind = _spawnVehicle switch { "bike" => VehicleKind.Bike, "boat" => VehicleKind.Boat, "plane" => VehicleKind.Plane, _ => VehicleKind.Car };
        _vehicles.Spawn(kind);
    }

    public override void _Process(double delta)
    {
        _frames++;
        _planet.UpdateSnow(_weather, _atmosphere.GameUtc); // neige au sol selon la météo réelle
        _planet.SunDirWorld = _sun.GlobalTransform.Basis.Z.Normalized();
        _buildings.Night = (float)Math.Clamp((2 - _atmosphere.SunElevationDeg) / 8, 0, 1); // fenêtres allumées au crépuscule
        _interiors.Night = _buildings.Night;
        var player = _camera.PlayerPos; // le personnage en mode marche, sinon la caméra
        double alt = player.Length() - Geo.Radius;
        double ground = _camera.Vehicle != null ? Vehicle.Ground(_camera.Vehicle.PosD, out _)
            : _camera.Walk && _camera.Body != null ? _camera.Body.GroundHeight : _camera.GroundHeight;
        var w = _atmosphere.Current;
        Vegetation.SetWind(w); // arbres et herbe balancés par le vent réel (uniforme global « wind »)
        Geo.ToLatLon(_camera.PosD.Normalized(), out double camLat, out double camLon);
        var local = TimeZones.LocalTime(_atmosphere.GameUtc, camLat, camLon, out _, out _, out _);
        _buildings.LocalHour = local.TimeOfDay.TotalHours;
        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);

        if (_shotPath != null)
        {
            if (_enterTest != null && EnterTest()) return;
            _idleFrames = _planet.Idle && _political.Ready && _roads.Idle && _places.Idle && _transit.Idle && _streets.Idle && _buildings.Idle && _ride.Loaded && StreamedRaster.Downloading == 0 && PmTilesArchive.Downloading == 0 ? _idleFrames + 1 : 0;
            if (_walkTest > 0 && ((_frames > _minFrames && _idleFrames > 20) || _camera.AutoForward))
            {
                if (!_camera.AutoForward)
                {
                    if (_spawnVehicle != null) { SpawnTestVehicle(); _spawnVehicle = null; } // véhicule de test : une fois le monde chargé (eau, relief fin)
                    else if (_camera.Vehicle == null) { _camera.SetWalk(true); if (_testPitch is double tp) _camera.Pitch = tp * Math.PI / 180; }
                    _camera.AutoForward = true;
                }
                _walkTest--;
                if (_walkTest % 10 == 0) GD.Print($"[Test] {(_camera.Vehicle != null ? _camera.Vehicle.Hud() : "marche")} : {lat:F6} {lon:F6} alt {alt:F1} sol {ground:F1} obstacles {Obstacles.Count} FPS {Engine.GetFramesPerSecond()}");
                if (_enterTest != null && _walkTest % 10 == 0) GD.Print($"[Test] {_interiors.Status} ; {Obstacles.Describe(_camera.PlayerPos)}");
                return;
            }
            if ((_frames > _minFrames && _idleFrames > 20) || _frames > 4000 || (_rideTest != null && _rideTest != "board" && _frames > 160))
            {
                _camera.AutoForward = false;
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(_shotPath);
                GD.Print($"[Main] capture enregistrée : {_shotPath} (après {_frames} images)");
                GetTree().Quit();
            }
        }
    }

    /// <summary>Panneau F3 : toutes les informations techniques (autrefois affichées en permanence en haut à gauche).</summary>
    string DebugText()
    {
        var player = _camera.PlayerPos;
        double alt = player.Length() - Geo.Radius;
        double ground = _camera.Vehicle != null ? Vehicle.Ground(_camera.Vehicle.PosD, out _)
            : _camera.Walk && _camera.Body != null ? _camera.Body.GroundHeight : _camera.GroundHeight;
        var w = _atmosphere.Current;
        var utc = _atmosphere.GameUtc;
        Geo.ToLatLon(_camera.PosD.Normalized(), out double camLat, out double camLon);
        double solarH = ((utc.TimeOfDay.TotalHours + camLon / 15) % 24 + 24) % 24;
        var local = TimeZones.LocalTime(utc, camLat, camLon, out string zone, out TimeSpan offset, out bool dst);
        string precip = w.PrecipMmH < 0.1 ? "pas de précipitations" : $"{(w.FrozenFraction > 0.5 || w.TemperatureC < 0.5 ? "neige" : "pluie")} {w.PrecipMmH:F1} mm/h";
        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);
        return
            $"Infos techniques (F3)   FPS {Engine.GetFramesPerSecond()}   graphismes : {GraphicsQuality.Name(GraphicsQuality.Level)}\n" +
            $"Lat {lat:F4}°  Lon {lon:F4}°   altitude {alt:N0} m   sol {ground:N0} m   hauteur {alt - ground:N0} m\n" +
            $"Vitesse {_camera.Speed * 3.6:N0} km/h  ×{_camera.SpeedMultiplier:F2}  {(_camera.Walk ? $"[MARCHE{(_camera.Grounded ? "" : ", en l'air")}, {(_camera.ThirdPerson ? "vue arrière" : "1re personne")}]" : "")}\n" +
            $"Chunks {_planet.VisibleChunks} visibles / {_planet.TotalChunks} (niveau max {_planet.DeepestLevel})   en construction {_planet.PendingBuilds}   relief : {(StreamedRaster.Downloading > 0 ? $"téléchargement ({StreamedRaster.Downloading})" : "à jour")}{(SwissAlti3D.Covers(lat, lon) ? "   zone HD 0,5 m (swissALTI3D © swisstopo)" : "")}\n" +
            $"Arbres {Vegetation.Instances:N0}   obstacles {Obstacles.Count:N0}   herbe {_ground.GrassInstances:N0}\n" +
            $"Heure locale {local:HH:mm} ({local.ToString("dddd d MMMM", Fr)}, {zone}, {TimeZones.FormatOffset(offset)}{(dst ? ", heure d'été" : "")})   {utc:HH:mm} UTC{(_atmosphere.TimeOffsetHours != 0 ? $" (décalage {_atmosphere.TimeOffsetHours:+0.##;-0.##} h)" : "")}   heure solaire {(int)solarH:00}:{(int)(solarH % 1 * 60):00}   soleil {_atmosphere.SunElevationDeg:F0}° {(_atmosphere.AlwaysDay ? "(jour permanent, J)" : _atmosphere.SunElevationDeg > 0 ? "(jour)" : _atmosphere.SunElevationDeg > -6 ? "(crépuscule)" : "(nuit)")}\n" +
            $"Météo ({_weather.Status}) : {w.TemperatureC:F1} °C   vent {w.Wind10Speed * 3.6:F0} km/h du {w.Wind10FromDeg:F0}° (rafales {w.Gust * 3.6:F0})   nuages bas/moy/hauts {w.CloudLow * 100:F0}/{w.CloudMid * 100:F0}/{w.CloudHigh * 100:F0} %   {precip}   visibilité {w.VisibilityM / 1000:F1} km\n" +
            $"Océan : {_ocean.Status}{(_ocean.Underwater ? "   [SOUS L'EAU]" : "")}\n" +
            $"Routes et rails : {_roads.Status}{(_roads.ShowLayer ? "" : " (masqués)")}   noms de lieux : {_places.Count}{(_places.ShowLayer ? "" : " (masqués)")}   rues : {_streets.Count}\n" +
            $"Bâtiments : {_buildings.Status} ({_interiors.Status})\n" +
            $"Arrêts et gares : {_transit.Count - _transit.Airports}, aéroports : {_transit.Airports}{(_transit.ShowLayer ? "" : " (masqués)")}   horaires : {_ride.Status}   {(PmTilesArchive.Downloading > 0 ? $"téléchargement OSM ({PmTilesArchive.Downloading})" : "")}" +
            (_camera.Vehicle != null ? "\n" + _camera.Vehicle.Hud() : "");
    }
}
