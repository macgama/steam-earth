using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Voyager en transports publics suisses aux horaires réels (opentransportdata.swiss) :
/// - H près d'un arrêt (≤ 250 m) : tableau des prochains départs (heure suisse du jeu, donc réelle par défaut) ;
///   1–9 pour choisir, flèches ↑/↓ pour voir plus tôt / plus tard, H ou Échap pour fermer.
/// - On attend le départ sur place (le véhicule arrive quelques minutes avant) ; on monte automatiquement à
///   l'heure si l'on est à moins de 300 m de l'arrêt. H pendant l'attente : renoncer.
/// - Pendant le voyage, le véhicule roule en temps réel selon l'horaire (accélère et freine entre deux
///   arrêts) ; la caméra le suit (molette = distance, V = vue du passager). H : descendre au prochain arrêt
///   (on descend aussi au terminus). Les touches + / − de l'heure font avancer le voyage plus vite.
/// Limite actuelle : le véhicule va en ligne droite d'un arrêt à l'autre en suivant le relief (pas encore
/// sur les vrais rails ni dans les tunnels).
/// </summary>
public partial class TransitRide : Node3D, IRide
{
    public FlyCamera Camera;
    public Planet Planet;
    public Atmosphere Atmosphere;

    enum State { Idle, Board, Waiting, Riding }
    State _state = State.Idle;

    Task<Timetable> _load;
    Timetable _tt;
    string _loadStatus = "chargement des horaires…";

    // tableau des départs
    int _station = -1;              // gare principale (nom du tableau)
    List<int> _stations = new();    // toutes les gares du pôle
    List<Timetable.Dep> _deps = new();
    int _boardOffset; // minutes de décalage du tableau (↑/↓)

    // course choisie
    int _trip = -1, _day, _from, _alightAt = -1;
    bool _stopRequested;
    double _lastMin;
    Vec3d _anchor, _lastAnchor;
    double _speed;
    string _message; double _messageUntil;

    MeshInstance3D _vehicle, _band;
    Vector3 _size;

    CanvasLayer _ui;
    PanelContainer _panel;
    Label _title, _body;

    public string Status => _tt == null ? _loadStatus : $"{_tt.Stations:N0} arrêts, {_tt.Trips:N0} courses (horaire {_tt.FeedVersion})";
    public bool Busy => _state != State.Idle;
    /// <summary>Le tableau des départs est affiché (il se ferme avec Échap).</summary>
    public bool BoardOpen => _state == State.Board;
    public bool Loaded => _load.IsCompleted;

    // IRide
    public Vec3d Anchor => _anchor;
    public double ViewDistance => _size.Z * 0.7 + 12;
    public double Speed => _speed;

    public override void _Ready()
    {
        string cache = System.IO.Path.Combine(OS.GetUserDataDir(), "cache", "timetable_ch.bin.gz");
        _load = Task.Run(() => Timetable.LoadAsync(cache, s => _loadStatus = s));

        _ui = new CanvasLayer { Layer = 5 };
        AddChild(_ui);
        _panel = new PanelContainer { Visible = false };
        var style = new StyleBoxFlat { BgColor = new Color(0.04f, 0.06f, 0.12f, 0.88f), ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 12 };
        style.SetCornerRadiusAll(8);
        _panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 20);
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        box.AddChild(_title);
        _body = new Label();
        _body.AddThemeFontSizeOverride("font_size", 15);
        // police à chasse fixe : colonnes alignées comme sur un vrai tableau de départs
        _body.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "Consolas", "DejaVu Sans Mono", "Menlo", "monospace" } });
        box.AddChild(_body);
        _ui.AddChild(_panel);
    }

    DateTime LocalNow() => TimeZones.LocalTime(Atmosphere.GameUtc, 46.95, 7.44, out _, out _, out _);
    double NowMin() => _tt.MinutesOf(LocalNow());

    // ------------------------------------------------------------------ entrées

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (Camera.InputBlocked && _state != State.Board) return; // un menu est ouvert
        if (_state == State.Board)
        {
            if (k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9 || k.PhysicalKeycode >= Key.Kp1 && k.PhysicalKeycode <= Key.Kp9)
            {
                int i = (int)(k.PhysicalKeycode >= Key.Kp1 ? k.PhysicalKeycode - Key.Kp1 : k.PhysicalKeycode - Key.Key1);
                if (i < _deps.Count) Choose(_deps[i]);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (k.PhysicalKeycode is Key.Down or Key.Pagedown) { _boardOffset += 20; RefreshBoard(); GetViewport().SetInputAsHandled(); return; }
            if (k.PhysicalKeycode is Key.Up or Key.Pageup) { _boardOffset = Math.Max(0, _boardOffset - 20); RefreshBoard(); GetViewport().SetInputAsHandled(); return; }
            if (k.PhysicalKeycode is Key.Escape or Key.H) { SetState(State.Idle); GetViewport().SetInputAsHandled(); return; }
        }
        if (k.PhysicalKeycode != Key.H) return;
        GetViewport().SetInputAsHandled();
        switch (_state)
        {
            case State.Idle: OpenBoard(); break;
            case State.Waiting: Say("Voyage annulé."); SetState(State.Idle); break;
            case State.Riding: _stopRequested = !_stopRequested; Say(_stopRequested ? "Arrêt demandé : vous descendrez au prochain arrêt." : "Demande d'arrêt annulée."); break;
        }
    }

    void Say(string msg) { GD.Print("[TransitRide] " + msg); _message = msg; _messageUntil = Time.GetTicksMsec() / 1000.0 + 6; }

    public void OpenBoard()
    {
        if (_tt == null) { Say(_loadStatus); return; }
        Geo.ToLatLon(Camera.PlayerPos.Normalized(), out double lat, out double lon);
        int nearest = _tt.NearestStation(lat, lon, 250, out _);
        if (nearest < 0) { Say("Aucun arrêt de transports publics suisses à moins de 250 m."); return; }
        // le pôle : les arrêts à moins de 200 m de l'arrêt le plus proche
        _stations = _tt.StationsNear(_tt.StationLat[nearest], _tt.StationLon[nearest], 200);
        _station = nearest;
        foreach (int s in _stations) if (_tt.Importance(s) > _tt.Importance(_station)) _station = s;
        _boardOffset = 0;
        SetState(State.Board);
        RefreshBoard();
        GD.Print($"[TransitRide] tableau des départs : {_tt.StationNameOf(_station)}, {_deps.Count} départs");
    }

    void RefreshBoard() => _deps = Board(_boardOffset, 9);

    List<Timetable.Dep> Board(double offset, int max)
    {
        double from = NowMin() + offset - 0.5;
        var all = new List<Timetable.Dep>();
        foreach (int s in _stations) all.AddRange(_tt.Departures(s, from, 240, max));
        all.Sort((x, y) => x.Minutes.CompareTo(y.Minutes));
        return all.Count > max ? all.GetRange(0, max) : all;
    }

    void Choose(Timetable.Dep d)
    {
        _trip = d.Trip; _day = d.Day; _from = d.Pos;
        _stopRequested = false;
        _alightAt = -1;
        SetState(State.Waiting);
        Say($"Rendez-vous {_tt.StationNameOf(_tt.StationAt(_trip, _from))}{Platform(_tt.StopAt(_trip, _from))} à {Hm(d.Minutes)}.");
    }

    /// <summary>Test automatique (--ride=LIGNE,minutes) : choisit le prochain départ de la ligne et avance l'heure
    /// du jeu pour être <paramref name="minutesAfter"/> minutes après le départ.</summary>
    public async void TestRide(string line, double minutesAfter)
    {
        while (_tt == null) await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        OpenBoard();
        if (_state != State.Board) { GD.PrintErr("[TransitRide] aucun arrêt pour le test"); return; }
        {
            foreach (var d in Board(0, 2000))
                if (_tt.S(_tt.RouteName[_tt.TripRoute[d.Trip]]) == line)
                {
                    Choose(d);
                    Atmosphere.TimeOffsetHours += (d.Minutes + minutesAfter - NowMin()) / 60.0;
                    if (minutesAfter > 0) Board(); // déjà parti : on est à bord
                    GD.Print($"[TransitRide] test : {Describe(d.Trip)} départ {Hm(d.Minutes)}, heure du jeu {LocalNow():HH:mm}");
                    return;
                }
        }
        GD.PrintErr($"[TransitRide] ligne {line} introuvable ici");
    }

    void SetState(State s)
    {
        _state = s;
        if (s == State.Idle) { _trip = -1; RemoveVehicle(); }
        if (s != State.Riding && Camera.Ride == this) Camera.Ride = null;
    }

    // ------------------------------------------------------------------ boucle

    public override void _Process(double delta)
    {
        if (_tt == null && _load.IsCompleted)
        {
            _tt = _load.Result;
            if (_tt != null) GD.Print($"[TransitRide] {Status}");
            else _loadStatus = "horaires indisponibles (" + _loadStatus + ")";
        }
        if (_tt != null && _trip >= 0) UpdateTrip();
        UpdateVehicle();
        UpdatePanel();
    }

    void UpdateTrip()
    {
        double now = NowMin();
        int n = _tt.PatternLength(_trip);
        int depMin = _tt.Departure(_trip, _day, _from);
        if (_state == State.Waiting)
        {
            double dist = (Camera.PlayerPos - StationPos(_tt.StationAt(_trip, _from), 0)).Length();
            if (now >= depMin - 0.3 && now <= depMin + 0.5)
            {
                if (dist < 300) Board();
            }
            else if (now > depMin + 0.5)
            {
                Say(dist < 300 ? "Départ manqué." : "Vous étiez trop loin de l'arrêt : départ manqué.");
                SetState(State.Idle);
                return;
            }
        }
        if (_state == State.Riding)
        {
            // Arrivé à un arrêt où l'on descend : arrêt demandé, ou terminus
            for (int k = _from + 1; k < n; k++)
            {
                int a = _tt.Arrival(_trip, _day, k), d = _tt.Departure(_trip, _day, k);
                if (now < a) break;
                bool last = k == n - 1;
                if (now >= a && (now <= d + 0.01 || last) && _tt.CanAlight(_trip, k) && (_stopRequested || last))
                {
                    Alight(k);
                    return;
                }
                if (now > d) _from = Math.Max(_from, k); // arrêt passé sans descendre
            }
        }
    }

    void Board()
    {
        if (Camera.Walk || Camera.Vehicle != null) Camera.SetWalk(false); // on laisse sa voiture ou son vélo à l'arrêt
        Camera.Ride = this;
        Camera.Yaw = VehicleYaw(NowMin()) + Math.PI * 0.15;
        Camera.Pitch = -0.25;
        _state = State.Riding;
        Say($"Vous êtes à bord : {Describe(_trip)}. H pour descendre au prochain arrêt.");
    }

    void Alight(int k)
    {
        int station = _tt.StationAt(_trip, k);
        double lat = _tt.StationLat[station], lon = _tt.StationLon[station];
        Camera.Ride = null;
        // On descend à côté du véhicule, à pied
        var side = SideOffset(lat, lon);
        Camera.Teleport(side.lat, side.lon, 1.8, Camera.Yaw * 180 / Math.PI, 0);
        Camera.SetWalk(true);
        Say($"Vous êtes descendu à {_tt.StationNameOf(station)} à {LocalNow():HH:mm}.");
        SetState(State.Idle);
    }

    (double lat, double lon) SideOffset(double lat, double lon)
    {
        double yaw = VehicleYaw(NowMin()) + Math.PI / 2; // à droite du véhicule
        double d = _size.X / 2 + 3;
        return (lat + Math.Cos(yaw) * d / 111_320, lon + Math.Sin(yaw) * d / (111_320 * Math.Cos(lat * Math.PI / 180)));
    }

    // ------------------------------------------------------------------ position du véhicule

    Vec3d StationPos(int station, double lift)
    {
        double lat = _tt.StationLat[station], lon = _tt.StationLon[station];
        return Geo.FromLatLon(lat, lon) * (Geo.Radius + HeightProvider.SurfaceHeight(lat, lon, 4) + lift);
    }

    /// <summary>Position au sol du véhicule à l'instant <paramref name="min"/> ; false s'il n'est pas en service.</summary>
    bool PositionAt(double min, out Vec3d pos)
    {
        pos = default;
        int n = _tt.PatternLength(_trip);
        if (min < _tt.Arrival(_trip, _day, 0) - 0.01 || min > _tt.Departure(_trip, _day, n - 1) + 1) return false;
        for (int k = 0; k < n; k++)
        {
            int a = _tt.Arrival(_trip, _day, k), d = _tt.Departure(_trip, _day, k);
            if (min <= d || k == n - 1) { pos = Ground(_tt.StationAt(_trip, k), -1, 0); return true; }
            int a2 = _tt.Arrival(_trip, _day, k + 1);
            if (min < a2)
            {
                double f = (min - d) / Math.Max(a2 - d, 0.01);
                f = f * f * (3 - 2 * f); // accélère au départ, freine à l'arrivée
                pos = Ground(_tt.StationAt(_trip, k), _tt.StationAt(_trip, k + 1), f);
                return true;
            }
        }
        return false;
    }

    Vec3d Ground(int s0, int s1, double f)
    {
        var a = Geo.FromLatLon(_tt.StationLat[s0], _tt.StationLon[s0]);
        var dir = s1 < 0 ? a : (a * (1 - f) + Geo.FromLatLon(_tt.StationLat[s1], _tt.StationLon[s1]) * f).Normalized();
        Geo.ToLatLon(dir, out double lat, out double lon);
        // même précaution que la caméra : jamais sous le relief affiché, même s'il n'est pas encore au plus fin
        double h = Math.Max(HeightProvider.SurfaceHeight(lat, lon, 4), HeightProvider.SurfaceHeight(lat, lon, 30));
        return dir * (Geo.Radius + h + 0.3);
    }

    double VehicleYaw(double min)
    {
        if (!PositionAt(min, out var p0)) return Camera.Yaw;
        // direction vers la position un peu plus tard (ou vers l'arrêt suivant à l'arrêt)
        Vec3d p1 = p0;
        for (double dt = 0.05; dt < 30 && (p1 - p0).Length() < 2; dt *= 2)
            if (!PositionAt(min + dt, out p1)) break;
        var up = p0.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var v = p1 - p0;
        if (v.Length() < 0.5) return _lastYaw;
        _lastYaw = Math.Atan2(v.Dot(east), v.Dot(north));
        return _lastYaw;
    }
    double _lastYaw;

    void UpdateVehicle()
    {
        if (_trip < 0 || _tt == null) { RemoveVehicle(); return; }
        double now = NowMin();
        // Le véhicule apparaît quand il est en route (au plus tôt 8 min avant le départ du joueur)
        bool show = (_state == State.Riding || now > _tt.Departure(_trip, _day, _from) - 8) && PositionAt(now, out var ground);
        if (!show) { if (_vehicle != null) _vehicle.Visible = false; return; }
        PositionAt(now, out ground);
        EnsureVehicle();
        double yaw = VehicleYaw(now);
        var up = ground.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var fwd = north * Math.Cos(yaw) + east * Math.Sin(yaw);
        var right = fwd.Cross(up).Normalized(); // est quand on va vers le nord
        var basis = new Basis(right.ToVector3(), up.ToVector3(), (-fwd).ToVector3()); // repère direct (boîte symétrique)
        var center = ground + up * (_size.Y / 2);
        _vehicle.Transform = new Transform3D(basis, (center - Camera.PosD).ToVector3());
        _vehicle.Visible = !(_state == State.Riding && !Camera.ThirdPerson);

        // place du passager : à l'avant, au-dessus du plancher
        var newAnchor = ground + up * (_size.Y * 0.75) + fwd * (_size.Z * 0.3);
        if (_lastMin > 0 && now > _lastMin) _speed = (newAnchor - _lastAnchor).Length() / ((now - _lastMin) * 60);
        _lastAnchor = newAnchor; _lastMin = now;
        _anchor = newAnchor;
    }

    void EnsureVehicle()
    {
        var mode = Timetable.ModeOf(_tt.RouteType[_tt.TripRoute[_trip]]);
        (Vector3 size, Color body, Color band) = mode switch
        {
            TransitLayer.Mode.Train => (new Vector3(3.0f, 4.2f, 100f), new Color(0.78f, 0.1f, 0.12f), new Color(0.1f, 0.12f, 0.15f)),
            TransitLayer.Mode.Tram => (new Vector3(2.4f, 3.4f, 32f), new Color(0.1f, 0.35f, 0.75f), new Color(0.1f, 0.12f, 0.15f)),
            TransitLayer.Mode.Metro => (new Vector3(2.8f, 3.6f, 60f), new Color(0.6f, 0.6f, 0.65f), new Color(0.1f, 0.12f, 0.15f)),
            TransitLayer.Mode.Ferry => (new Vector3(9f, 5f, 45f), new Color(0.95f, 0.95f, 0.95f), new Color(0.15f, 0.25f, 0.5f)),
            TransitLayer.Mode.Funicular or TransitLayer.Mode.CableCar => (new Vector3(3f, 3.2f, 10f), new Color(0.85f, 0.15f, 0.15f), new Color(0.1f, 0.12f, 0.15f)),
            _ => (new Vector3(2.55f, 3.2f, 12f), new Color(0.98f, 0.8f, 0.1f), new Color(0.1f, 0.12f, 0.15f)), // car postal
        };
        if (_vehicle != null && _size == size) return;
        RemoveVehicle();
        _size = size;
        _vehicle = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = body, Roughness = 0.5f } };
        // bande de fenêtres
        _band = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(size.X + 0.06f, size.Y * 0.28f, size.Z * 0.96f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = band, Roughness = 0.2f, Metallic = 0.3f },
            Position = new Vector3(0, size.Y * 0.15f, 0),
        };
        _vehicle.AddChild(_band);
        Planet.AddChild(_vehicle);
    }

    void RemoveVehicle()
    {
        _vehicle?.QueueFree();
        _vehicle = null; _band = null;
        _lastMin = 0;
    }

    // ------------------------------------------------------------------ affichage

    static string Hm(double minutes)
    {
        int m = (int)Math.Floor(minutes) % 1440;
        if (m < 0) m += 1440;
        return $"{m / 60:00}:{m % 60:00}";
    }

    string Platform(int stop)
    {
        string p = _tt.S(_tt.StopPlatform[stop]);
        return p.Length > 0 ? $", voie {p}" : "";
    }

    string Describe(int trip)
    {
        string line = _tt.S(_tt.RouteName[_tt.TripRoute[trip]]);
        string num = _tt.S(_tt.TripNumber[trip]);
        string head = _tt.S(_tt.TripHeadsign[trip]);
        if (head.Length == 0) head = _tt.StationNameOf(_tt.StationAt(trip, _tt.PatternLength(trip) - 1));
        var mode = Timetable.ModeOf(_tt.RouteType[_tt.TripRoute[trip]]);
        string what = mode switch
        {
            TransitLayer.Mode.Train => "train", TransitLayer.Mode.Tram => "tram", TransitLayer.Mode.Metro => "métro",
            TransitLayer.Mode.Ferry => "bateau", TransitLayer.Mode.Funicular => "funiculaire", TransitLayer.Mode.CableCar => "téléphérique",
            _ => "bus",
        };
        return $"{what} {line}{(mode == TransitLayer.Mode.Train && num.Length > 0 && num != line ? $" ({num})" : "")} → {head}";
    }

    static string Pad(string s, int n) => s.Length > n ? s.Substring(0, n - 1) + "…" : s.PadRight(n);

    void UpdatePanel()
    {
        bool msg = _message != null && Time.GetTicksMsec() / 1000.0 < _messageUntil;
        if (_tt == null || _state == State.Idle)
        {
            _panel.Visible = msg;
            if (msg) { _title.Text = "Transports publics"; _body.Text = _message; PlacePanel(); }
            return;
        }
        _panel.Visible = true;
        double now = NowMin();
        var sb = new System.Text.StringBuilder();
        switch (_state)
        {
            case State.Board:
            {
                if ((int)(now * 2) != (int)(_lastBoardRefresh * 2)) { RefreshBoard(); _lastBoardRefresh = now; }
                _title.Text = $"Départs — {_tt.StationNameOf(_station)}   {LocalNow():HH:mm}";
                sb.AppendLine("    Heure  Ligne    Destination                    Voie   Dans");
                for (int i = 0; i < _deps.Count; i++)
                {
                    var d = _deps[i];
                    string line = _tt.S(_tt.RouteName[_tt.TripRoute[d.Trip]]);
                    string head = _tt.S(_tt.TripHeadsign[d.Trip]);
                    if (head.Length == 0) head = _tt.StationNameOf(_tt.StationAt(d.Trip, _tt.PatternLength(d.Trip) - 1));
                    string plat = _tt.S(_tt.StopPlatform[_tt.StopAt(d.Trip, d.Pos)]);
                    double inMin = d.Minutes - now;
                    sb.AppendLine($"[{i + 1}] {Hm(d.Minutes)}  {Pad(line, 7)}  {Pad(head, 30)} {Pad(plat, 5)}  {(inMin < 1 ? "départ" : $"{inMin:0} min")}");
                }
                if (_deps.Count == 0) sb.AppendLine("Aucun départ dans les 4 prochaines heures.");
                sb.Append("\n1–9 : choisir   ↑/↓ : plus tôt / plus tard   H ou Échap : fermer");
                break;
            }
            case State.Waiting:
            {
                int dep = _tt.Departure(_trip, _day, _from);
                _title.Text = Describe(_trip);
                double dist = (Camera.PlayerPos - StationPos(_tt.StationAt(_trip, _from), 0)).Length();
                sb.AppendLine($"Départ de {_tt.StationNameOf(_tt.StationAt(_trip, _from))}{Platform(_tt.StopAt(_trip, _from))} à {Hm(dep)}");
                sb.AppendLine($"dans {Math.Max(0, dep - now):0.0} min (il est {LocalNow():HH:mm:ss})   arrêt à {dist:N0} m{(dist > 300 ? " : rapprochez-vous (< 300 m) !" : "")}");
                AppendStops(sb, _from, now, 8);
                sb.Append("\nJ : renoncer   (+ / − : avancer / reculer l'heure)");
                break;
            }
            case State.Riding:
            {
                _title.Text = Describe(_trip);
                sb.AppendLine($"{LocalNow():HH:mm:ss}   {_speed * 3.6:0} km/h");
                AppendStops(sb, _from, now, 8);
                sb.Append(_stopRequested ? "\nArrêt demandé ✓ (H pour annuler)" : "\nH : descendre au prochain arrêt   molette : distance   V : vue passager");
                break;
            }
        }
        if (msg) sb.Append("\n\n" + _message);
        _body.Text = sb.ToString();
        PlacePanel();
    }
    double _lastBoardRefresh;

    /// <summary>À droite de l'écran, sous le bouton « Pays ».</summary>
    void PlacePanel()
    {
        _panel.ResetSize();
        var vp = GetViewport().GetVisibleRect().Size;
        _panel.Position = new Vector2(vp.X - _panel.Size.X - 16, Math.Max(60, (vp.Y - _panel.Size.Y) * 0.45f));
    }

    void AppendStops(System.Text.StringBuilder sb, int from, double now, int count)
    {
        int n = _tt.PatternLength(_trip), shown = 0;
        for (int k = from; k < n && shown < count; k++)
        {
            int a = _tt.Arrival(_trip, _day, k);
            if (k > from && a < now - 0.01 && _tt.Departure(_trip, _day, k) < now) continue; // déjà passé
            string name = _tt.StationNameOf(_tt.StationAt(_trip, k));
            string mark = k == from && _state == State.Waiting ? "▶" : now >= a && now <= _tt.Departure(_trip, _day, k) ? "●" : " ";
            sb.AppendLine($" {mark} {Hm(k == from ? _tt.Departure(_trip, _day, k) : a)}  {Pad(name, 34)}{(_tt.CanAlight(_trip, k) ? "" : " (montée seulement)")}");
            shown++;
        }
        int rest = n - from - shown;
        if (rest > 0) sb.AppendLine($"   … et {rest} arrêts, terminus {Hm(_tt.Arrival(_trip, _day, n - 1))} {_tt.StationNameOf(_tt.StationAt(_trip, n - 1))}");
    }
}
