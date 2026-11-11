using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Menu de départ (au lancement, puis touche M) : le joueur commence où il veut sur la planète.
/// - « Reprendre » : là où il était à la fin de la partie précédente (user://last_position.json) ;
/// - recherche parmi toutes les villes et tous les villages de la Terre (~1,7 million, OpenStreetMap,
///   <see cref="Localities"/>) et les pays : la localité choisie est construite ;
/// - clic sur la Terre : on zoome vers ce point (de l'orbite jusqu'à ~20 km), puis un dernier clic y atterrit.
/// </summary>
public partial class StartMenu : CanvasLayer
{
    public FlyCamera Camera;
    public BuildingsLayer Buildings;

    sealed class SavedPos { public double Lat { get; set; } public double Lon { get; set; } public double Height { get; set; } public double Yaw { get; set; } }

    PanelContainer _panel;
    LineEdit _search;
    VBoxContainer _list;
    Button _resume, _up;
    Label _hint;
    SavedPos _saved;
    double _lookLat = 46.8, _lookLon = 8.2, _lookHeight = 9_000_000;
    double _saveClock;

    const double OrbitHeight = 9_000_000, LandHeight = 25_000;

    public bool IsOpen => _panel.Visible;
    static string SavePath => Path.Combine(OS.GetUserDataDir(), "last_position.json");

    public override void _Ready()
    {
        Layer = 3;
        LoadCities();
        try { if (File.Exists(SavePath)) _saved = JsonSerializer.Deserialize<SavedPos>(File.ReadAllText(SavePath)); }
        catch (Exception e) { GD.PrintErr($"[StartMenu] {SavePath} : {e.Message}"); }

        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _panel.OffsetLeft = 16; _panel.OffsetRight = 436; _panel.OffsetTop = 16; _panel.OffsetBottom = -16;
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.92f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 12, ContentMarginBottom = 12, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomRight = 8 });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        var title = new Label { Text = "Où veux-tu commencer ?" };
        title.AddThemeFontSizeOverride("font_size", 24);
        box.AddChild(title);

        _resume = new Button { FocusMode = Control.FocusModeEnum.None };
        _resume.Pressed += Resume;
        box.AddChild(_resume);

        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _hint.AddThemeColorOverride("font_color", new Color(1, 0.88f, 0.55f));
        box.AddChild(_hint);

        _up = new Button { Text = "Reculer (vue plus large)", FocusMode = Control.FocusModeEnum.None };
        _up.Pressed += ZoomOut;
        box.AddChild(_up);

        _search = new LineEdit { PlaceholderText = "Chercher une ville ou un pays…", ClearButtonEnabled = true };
        _search.TextChanged += Filter;
        _search.TextSubmitted += _ => { foreach (var c in _list.GetChildren()) if (c is Button b) { b.EmitSignal(BaseButton.SignalName.Pressed); break; } };
        box.AddChild(_search);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_list);

        var close = new Button { Text = "Fermer (M)", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += Close;
        box.AddChild(close);
        Filter("");
    }

    void LoadCities()
    {
        try { Localities.Init(Godot.FileAccess.GetFileAsBytes("res://data/geo/localities.tsv.br")); }
        catch (Exception e) { GD.PrintErr($"[StartMenu] localités : {e.Message}"); }
        foreach (var c in Geography.Countries) if (!string.IsNullOrEmpty(c.Iso)) _countryNames[c.Iso] = c.Name;
        // Natural Earth n'a pas de code à deux lettres pour ces pays (code à trois lettres à la place)
        _countryNames["FR"] = "France"; _countryNames["NO"] = "Norvège"; _countryNames["XK"] = "Kosovo";
    }

    readonly Dictionary<string, string> _countryNames = new();
    string _shownQuery;   // recherche affichée (refaite quand l'index finit de charger)
    bool _shownReady;

    static string Normalize(string s) => Localities.Normalize(s);

    /// <summary>Résultats : toutes les villes et villages de la Terre (nom exact, puis début du nom, puis le reste ;
    /// les villes les plus peuplées d'abord), puis les pays.</summary>
    void Filter(string text)
    {
        _shownQuery = text; _shownReady = Localities.Ready;
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var q = Normalize(text.Trim());
        if (!Localities.Ready)
        {
            AddLabel(Localities.Error != null ? Loc.T("Liste des villes indisponible : ") + Localities.Error : Loc.T("Chargement des villes et villages du monde…"));
            if (q.Length == 0) return;
        }
        else if (q.Length == 0)
        {
            AddLabel(string.Format(Loc.T("Grandes villes ({0} villes et villages au total, tape un nom) :"), Localities.Count.ToString("N0", Loc.Culture)));
            for (int i = 0; i < 25 && i < Localities.Count; i++) AddCity(Localities.Get(i));
            return;
        }
        foreach (var i in Localities.Search(text, 60)) AddCity(Localities.Get(i));
        foreach (var k in Geography.Countries)
        {
            if (!Normalize(k.Name).Contains(q)) continue;
            var country = k;
            var b = new Button { Text = string.Format(Loc.T("{0} — capitale {1}"), k.Name, k.Capital), Alignment = HorizontalAlignment.Left, Flat = true, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => GoToCity(country.Capital, country.Lat, country.Lon, BuildingsLayer.RadiusFor("capital", 0), BuildingsLayer.MaxRadiusFor("capital", 0));
            _list.AddChild(b);
        }
        if (_list.GetChildCount() == 0) AddLabel(Loc.T("Aucun résultat : clique plutôt sur la Terre."));
    }

    void AddLabel(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.6f));
        _list.AddChild(l);
    }

    void AddCity(Localities.Entry c)
    {
        string hab = Loc.T("hab.");
        string pop = c.Population >= 1e6 ? $"{c.Population / 1e6:0.#} M {hab}" : c.Population >= 1000 ? $"{c.Population / 1000:0} k {hab}" : c.Population > 0 ? $"{c.Population:0} {hab}" : "";
        string kind = Loc.T(c.Class switch { 'c' => "ville", 't' => "ville", 'v' => "village", _ => "localité" });
        string country = _countryNames.TryGetValue(c.Country, out var cn) ? cn : c.Country;
        string where = c.Region.Length > 0 && !char.IsDigit(c.Region[0]) ? $"{country}, {c.Region}" : country;
        string other = c.Other.Length > 0 ? $" « {c.Other} »" : "";
        var b = new Button
        {
            Text = $"{c.Name}{other} — {where} · {kind}{(pop.Length > 0 ? ", " + pop : "")}",
            Alignment = HorizontalAlignment.Left, Flat = true, FocusMode = Control.FocusModeEnum.None,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = $"{c.Name}{other}\n{where}\n{c.Lat:0.0000}, {c.Lon:0.0000}",
        };
        string cls = c.Class switch
        {
            'c' => c.Population >= 5e6 ? "megacity" : "city",
            't' => "town",
            'v' => "village",
            _ => c.Population >= 50_000 ? "city" : c.Population >= 5_000 ? "town" : "village",
        };
        b.Pressed += () => GoToCity(c.Name, c.Lat, c.Lon, BuildingsLayer.RadiusFor(cls, c.Population), BuildingsLayer.MaxRadiusFor(cls, c.Population));
        _list.AddChild(b);
    }

    // ------------------------------------------------------------------ ouverture, déplacements

    /// <param name="atLaunch">au lancement, « Reprendre » renvoie à la fin de la partie précédente ;
    /// ensuite, à l'endroit où l'on était avant d'ouvrir le menu.</param>
    public void Open(bool atLaunch = false)
    {
        if (!atLaunch)
        {
            Geo.ToLatLon(Camera.PlayerPos.Normalized(), out double la, out double lo);
            _saved = new SavedPos { Lat = la, Lon = lo, Height = Math.Max(0, Camera.Altitude - Camera.GroundHeight), Yaw = Camera.Yaw * 180 / Math.PI };
        }
        if (Camera.Walk || Camera.Vehicle != null) Camera.SetWalk(false);
        Geo.ToLatLon(Camera.PlayerPos.Normalized(), out _lookLat, out _lookLon);
        _lookHeight = OrbitHeight;
        Look();
        _panel.Visible = true;
        Camera.InputBlocked = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _resume.Visible = _saved != null;
        if (_saved != null) _resume.Text = $"Reprendre là où j'étais ({Math.Abs(_saved.Lat):0.00}°{(_saved.Lat >= 0 ? "N" : "S")}, {Math.Abs(_saved.Lon):0.00}°{(_saved.Lon >= 0 ? "E" : "O")})";
        _search.CallDeferred(Control.MethodName.GrabFocus);
        // test automatique : --search=texte
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--search=")) { _search.Text = arg.Substring(9); Filter(_search.Text); }
    }

    public void Close()
    {
        _panel.Visible = false;
        Camera.InputBlocked = false;
        _search.ReleaseFocus();
    }

    /// <summary>Vue verticale au-dessus du point visé.</summary>
    void Look()
    {
        Camera.Teleport(_lookLat, _lookLon, _lookHeight, 0, -89.9);
        _up.Visible = _lookHeight < OrbitHeight;
        _hint.Text = _lookHeight > LandHeight * 1.5
            ? "Ou clique sur la Terre pour t'en approcher, puis encore jusqu'à l'endroit exact."
            : "Clique sur l'endroit où atterrir.";
    }

    void ZoomOut()
    {
        _lookHeight = Math.Min(_lookHeight * 12, OrbitHeight);
        Look();
    }

    /// <summary>Échap : retour là où l'on était avant d'ouvrir le menu.</summary>
    public void Cancel()
    {
        if (_saved != null) Resume(); else Close();
    }

    /// <summary>Mémorise tout de suite la position (avant de revenir à l'écran titre).</summary>
    public void SaveNow()
    {
        Geo.ToLatLon(Camera.PlayerPos.Normalized(), out double lat, out double lon);
        _saved = new SavedPos { Lat = lat, Lon = lon, Height = Math.Max(0, Camera.Altitude - Camera.GroundHeight), Yaw = Camera.Yaw * 180 / Math.PI };
        try { File.WriteAllText(SavePath, JsonSerializer.Serialize(_saved)); } catch (Exception) { }
    }

    void Resume()
    {
        Close();
        Camera.Teleport(_saved.Lat, _saved.Lon, Math.Max(_saved.Height, 2), _saved.Yaw, -10);
    }

    /// <summary>500 m au-dessus de la ville, reculé de 1,5 km vers le sud pour la voir devant soi ; la ville est construite.</summary>
    void GoToCity(string name, double lat, double lon, double radius, double maxRadius)
    {
        Close();
        Land(lat, lon, 500, 1500);
        Buildings?.Build(name, lat, lon, radius, maxRadius);
        GD.Print($"[StartMenu] départ : {name} ({lat:F4}, {lon:F4})");
    }

    void Land(double lat, double lon, double height, double back)
    {
        var up = Geo.FromLatLon(lat, lon);
        Geo.LocalFrame(up, out _, out var north);
        Geo.ToLatLon((up * Geo.Radius - north * back).Normalized(), out double la, out double lo);
        Camera.Teleport(la, lo, height + HeightProvider.SurfaceHeight(lat, lon, 30) - HeightProvider.SurfaceHeight(la, lo, 30), 0, back > 0 ? -18 : -10);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.M && !_search.HasFocus())
        {
            if (IsOpen) Close(); else Open();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!IsOpen) return;
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left && PickGlobe(mb.Position, out double lat, out double lon))
            {
                if (_lookHeight <= LandHeight * 1.5)
                {
                    Close();
                    Land(lat, lon, 300, 0);
                    GD.Print($"[StartMenu] départ : {lat:F4}, {lon:F4}");
                }
                else
                {
                    _lookLat = lat; _lookLon = lon;
                    _lookHeight = Math.Max(_lookHeight / 12, LandHeight);
                    Look();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right) ZoomOut();
            GetViewport().SetInputAsHandled(); // pas de capture de la souris tant que le menu est ouvert
        }
    }

    /// <summary>Point de la Terre sous le pointeur (sphère au niveau de la mer).</summary>
    bool PickGlobe(Vector2 screen, out double lat, out double lon)
    {
        lat = lon = 0;
        var rayGodot = Camera.ProjectRayNormal(screen);
        var d = Vec3d.From(Camera.WorldRotation.Transposed() * rayGodot).Normalized(); // repère planète
        var o = Camera.PosD;
        double b = o.Dot(d), c = o.Dot(o) - Geo.Radius * Geo.Radius;
        double disc = b * b - c;
        if (disc < 0) return false;
        double t = -b - Math.Sqrt(disc);
        if (t < 0) return false;
        Geo.ToLatLon((o + d * t).Normalized(), out lat, out lon);
        return true;
    }

    public override void _Process(double delta)
    {
        // la liste des localités vient de finir de charger : on refait la recherche affichée
        if (IsOpen && !_shownReady && Localities.Ready) Filter(_shownQuery ?? "");
        // Position gardée pour « Reprendre » (toutes les 5 s, pas pendant le menu)
        _saveClock += delta;
        if (_saveClock < 5 || IsOpen || GameUi.MenuOpen) return; // pas l'orbite de l'écran titre
        _saveClock = 0;
        Geo.ToLatLon(Camera.PlayerPos.Normalized(), out double lat, out double lon);
        var p = new SavedPos { Lat = lat, Lon = lon, Height = Math.Max(0, Camera.Altitude - Camera.GroundHeight), Yaw = Camera.Yaw * 180 / Math.PI };
        try { File.WriteAllText(SavePath, JsonSerializer.Serialize(p)); } catch (Exception) { }
    }
}
