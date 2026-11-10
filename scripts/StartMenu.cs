using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Godot;

namespace SteamEarth;

/// <summary>
/// Menu de départ (au lancement, puis touche M) : le joueur commence où il veut sur la planète.
/// - « Reprendre » : là où il était à la fin de la partie précédente (user://last_position.json) ;
/// - recherche parmi ~7 300 villes du monde (Natural Earth) et les pays : la ville choisie est construite ;
/// - clic sur la Terre : on zoome vers ce point (de l'orbite jusqu'à ~20 km), puis un dernier clic y atterrit.
/// </summary>
public partial class StartMenu : CanvasLayer
{
    public FlyCamera Camera;
    public BuildingsLayer Buildings;

    sealed class CityEntry { public string Name, Country, Search; public double Lat, Lon, Pop; }
    sealed class SavedPos { public double Lat { get; set; } public double Lon { get; set; } public double Height { get; set; } public double Yaw { get; set; } }

    readonly List<CityEntry> _cities = new();
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
        try
        {
            var raw = Godot.FileAccess.GetFileAsBytes("res://data/geo/cities.tsv.gz");
            using var z = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var r = new StreamReader(z);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var f = line.Split('\t');
                if (f.Length < 5) continue;
                _cities.Add(new CityEntry
                {
                    Name = f[0], Country = f[1], Lat = double.Parse(f[2], inv), Lon = double.Parse(f[3], inv), Pop = double.Parse(f[4], inv),
                    Search = Normalize(f[0] + " " + f[1]),
                });
            }
        }
        catch (Exception e) { GD.PrintErr($"[StartMenu] villes : {e.Message}"); }
    }

    static string Normalize(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Résultats : villes (les plus peuplées d'abord ; celles dont le nom commence par la recherche en tête), puis pays.</summary>
    void Filter(string text)
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var q = Normalize(text.Trim());
        if (q.Length == 0)
        {
            AddLabel("Grandes villes :");
            for (int i = 0; i < 25 && i < _cities.Count; i++) AddCity(_cities[i]);
            return;
        }
        int n = 0;
        foreach (bool prefix in new[] { true, false })
            foreach (var c in _cities)
            {
                if (n >= 60) break;
                bool starts = c.Search.StartsWith(q);
                if (prefix ? !starts : starts || !c.Search.Contains(q)) continue;
                AddCity(c); n++;
            }
        foreach (var k in Geography.Countries)
        {
            if (!Normalize(k.Name).Contains(q)) continue;
            var country = k;
            var b = new Button { Text = $"{k.Name} — capitale {k.Capital}", Alignment = HorizontalAlignment.Left, Flat = true, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => GoToCity(country.Capital, country.Lat, country.Lon, BuildingsLayer.RadiusFor("capital", 0), BuildingsLayer.MaxRadiusFor("capital", 0));
            _list.AddChild(b);
        }
        if (_list.GetChildCount() == 0) AddLabel("Aucun résultat : clique plutôt sur la Terre.");
    }

    void AddLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.6f));
        _list.AddChild(l);
    }

    void AddCity(CityEntry c)
    {
        string pop = c.Pop >= 1e6 ? $"{c.Pop / 1e6:0.#} M hab." : c.Pop >= 1000 ? $"{c.Pop / 1000:0} k hab." : "";
        var b = new Button { Text = $"{c.Name} ({c.Country})  {pop}", Alignment = HorizontalAlignment.Left, Flat = true, FocusMode = Control.FocusModeEnum.None };
        string cls = c.Pop >= 5e6 ? "megacity" : c.Pop >= 50_000 ? "city" : c.Pop >= 5_000 ? "town" : "village";
        b.Pressed += () => GoToCity(c.Name, c.Lat, c.Lon, BuildingsLayer.RadiusFor(cls, c.Pop), BuildingsLayer.MaxRadiusFor(cls, c.Pop));
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
        // Position gardée pour « Reprendre » (toutes les 5 s, pas pendant le menu)
        _saveClock += delta;
        if (_saveClock < 5 || IsOpen) return;
        _saveClock = 0;
        Geo.ToLatLon(Camera.PlayerPos.Normalized(), out double lat, out double lon);
        var p = new SavedPos { Lat = lat, Lon = lon, Height = Math.Max(0, Camera.Altitude - Camera.GroundHeight), Yaw = Camera.Yaw * 180 / Math.PI };
        try { File.WriteAllText(SavePath, JsonSerializer.Serialize(p)); } catch (Exception) { }
    }
}
