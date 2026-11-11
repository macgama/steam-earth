using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// HUD discret pendant le jeu : carte en bas à gauche (vitesse, altitude, heure locale, météo, véhicule),
/// boussole en haut au centre, rappel des touches au début, mention OpenStreetMap.
/// Les informations techniques (anciennement affichées en permanence) sont dans un panneau à part (F3).
/// </summary>
public partial class GameHud : CanvasLayer
{
    public FlyCamera Camera;
    public Atmosphere Atmosphere;
    public Weather Weather;
    /// <summary>Texte du panneau F3, construit par Main (il connaît toutes les couches).</summary>
    public Func<string> DebugText;
    /// <summary>Un menu plein écran est ouvert : rien n'est affiché.</summary>
    public bool Hidden;

    PanelContainer _card, _debugPanel;
    Label _speed, _unit, _mode, _alt, _time, _weather, _vehicle, _vehicleHint, _debug, _hint, _osm;
    Compass _compass;
    double _hintTime = -1;
    double _slow = 1e9;

    public override void _Ready()
    {
        Layer = 1;

        // --- carte en bas à gauche
        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.CardBg, 10, 16, 10));
        _card.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _card.GrowVertical = Control.GrowDirection.Begin;
        _card.OffsetLeft = 18; _card.OffsetBottom = -18;
        _card.CustomMinimumSize = new Vector2(300, 0);
        _card.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_card);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        _card.AddChild(box);

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 6);
        box.AddChild(top);
        _speed = Text(34, UiTheme.Text, bold: true);
        top.AddChild(_speed);
        _unit = Text(15, UiTheme.TextDim);
        _unit.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        _unit.CustomMinimumSize = new Vector2(0, 0);
        top.AddChild(_unit);
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        _mode = Text(13, UiTheme.Accent, bold: true);
        _mode.Uppercase = true;
        _mode.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        top.AddChild(_mode);

        _alt = Text(15, UiTheme.Text);
        box.AddChild(_alt);
        _time = Text(15, UiTheme.Text);
        box.AddChild(_time);
        _weather = Text(15, UiTheme.Text);
        box.AddChild(_weather);
        _vehicle = Text(14, UiTheme.Text);
        _vehicle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _vehicle.CustomMinimumSize = new Vector2(380, 0);
        box.AddChild(_vehicle);
        _vehicleHint = Text(12, UiTheme.TextDim);
        _vehicleHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _vehicleHint.CustomMinimumSize = new Vector2(380, 0);
        box.AddChild(_vehicleHint);

        // --- boussole
        _compass = new Compass { CustomMinimumSize = new Vector2(420, 34), MouseFilter = Control.MouseFilterEnum.Ignore };
        _compass.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _compass.OffsetLeft = -210; _compass.OffsetRight = 210; _compass.OffsetTop = 12; _compass.OffsetBottom = 46;
        AddChild(_compass);

        // --- rappel des touches au début de la partie
        _hint = UiTheme.Overlay(15, UiTheme.TextDim);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.KeepSize, 22);
        _hint.GrowHorizontal = Control.GrowDirection.Both;
        _hint.GrowVertical = Control.GrowDirection.Begin;
        _hint.Visible = false;
        AddChild(_hint);

        // --- mention obligatoire des données OpenStreetMap (ODbL) ; la liste complète est dans « Crédits »
        _osm = UiTheme.Overlay(12, new Color(1, 1, 1, 0.55f));
        _osm.Text = "© OpenStreetMap contributors · Crédits dans le menu";
        _osm.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 8);
        _osm.GrowHorizontal = Control.GrowDirection.Begin;
        _osm.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_osm);

        // --- informations techniques (F3)
        _debugPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _debugPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0, 0, 0, 0.55f), 6, 12, 8));
        _debugPanel.Position = new Vector2(12, 12);
        AddChild(_debugPanel);
        _debug = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, MouseFilter = Control.MouseFilterEnum.Ignore };
        _debug.AddThemeFontSizeOverride("font_size", 13);
        _debug.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 0.85f));
        _debugPanel.AddChild(_debug);
    }

    Label Text(int size, Color color, bool bold = false)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (bold) l.AddThemeFontOverride("font", UiTheme.Bold);
        return l;
    }

    /// <summary>Montre « Échap : menu · F1 : commandes » quelques secondes (début de partie).</summary>
    public void ShowStartHint() => _hintTime = 12;

    public override void _Process(double delta)
    {
        bool show = !Hidden && UiSettings.ShowHud;
        _card.Visible = show;
        _compass.Visible = show && UiSettings.ShowCompass;
        _osm.Visible = !Hidden;
        _debugPanel.Visible = !Hidden && UiSettings.ShowDebug;

        if (_hintTime > 0 && !Hidden)
        {
            _hintTime -= delta;
            _hint.Text = Loc.T("Échap : menu") + "     " + Loc.T("F1 : commandes") + "     " + Loc.T("F2 : masquer le HUD");
            _hint.Visible = true;
            _hint.Modulate = new Color(1, 1, 1, (float)Math.Clamp(_hintTime / 1.5, 0, 1));
        }
        else _hint.Visible = false;

        if (_debugPanel.Visible && DebugText != null)
        {
            float sw = GetViewport().GetVisibleRect().Size.X;
            _debug.CustomMinimumSize = new Vector2(_compass.Visible ? sw / 2 - 250 : Math.Min(900, sw * 0.62f), 0); // sans recouvrir la boussole
            _debug.Text = DebugText();
            _debugPanel.ResetSize();
        }
        if (!show || Camera == null) return;

        _compass.Heading = Camera.Vehicle != null ? Camera.Vehicle.HeadingYaw : Camera.Yaw;

        // --- vitesse et mode
        _speed.Text = UiSettings.SpeedValue(Camera.Speed);
        _unit.Text = UiSettings.SpeedUnit;
        _mode.Text = Camera.Vehicle != null ? Loc.T(Camera.Vehicle.Label)
            : Camera.Ride != null ? Loc.T("Transports publics")
            : Camera.Walk ? Loc.T(Camera.Grounded ? "À pied" : "En l'air")
            : Loc.T("Vol libre");
        if (Camera.Ride == null && !SwissZone.Contains(Camera.PlayerPos)) _mode.Text += "  ·  " + Loc.T("survol");

        var player = Camera.PlayerPos;
        double alt = player.Length() - Geo.Radius;
        double ground = Camera.Vehicle != null ? Vehicle.Ground(Camera.Vehicle.PosD, out _)
            : Camera.Walk && Camera.Body != null ? Camera.Body.GroundHeight : Camera.GroundHeight;
        double agl = alt - ground;
        _alt.Text = Loc.T("Altitude") + " " + UiSettings.Length(alt) + (agl > 3 ? "   ·   " + string.Format(Loc.T("{0} au-dessus du sol"), UiSettings.Length(agl)) : "");

        // --- véhicule : la ligne d'état sans le rappel des touches, qui passe en petit dessous
        if (Camera.Vehicle != null)
        {
            string h = Camera.Vehicle.Hud();
            int i = h.IndexOf("   [", StringComparison.Ordinal);
            _vehicle.Text = (i >= 0 ? h[..i] : h).Replace("   ", "  ·  ");
            _vehicleHint.Text = i >= 0 ? h[(i + 4)..].TrimEnd(']') : "";
            _vehicle.Visible = true;
            _vehicleHint.Visible = _vehicleHint.Text.Length > 0;
        }
        else _vehicle.Visible = _vehicleHint.Visible = false;

        // --- heure et météo (deux fois par seconde suffit)
        _slow += delta;
        if (_slow < 0.5 || Atmosphere == null) return;
        _slow = 0;
        var utc = Atmosphere.GameUtc;
        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);
        var local = TimeZones.LocalTime(utc, lat, lon, out _, out _, out _);
        string day = local.ToString("dddd d MMMM", Loc.Culture);
        _time.Text = $"{local:HH:mm}   ·   {day}" + (Atmosphere.TimeOffsetHours != 0 ? "   ·   " + Loc.T("heure décalée") : "");

        var w = Atmosphere.Current;
        if (Weather == null || !Weather.HasData) { _weather.Text = Loc.T("Météo : chargement…"); return; }
        _weather.Text = $"{Condition(w)}   ·   {UiSettings.Temperature(w.TemperatureC)}   ·   {Loc.T("vent")} {UiSettings.Speed(w.Wind10Speed)} {Cardinal(w.Wind10FromDeg)}";
    }

    static string Condition(WeatherSample w)
    {
        if (w.PrecipMmH >= 0.1f)
        {
            bool snow = w.FrozenFraction > 0.5f || w.TemperatureC < 0.5f;
            if (snow) return Loc.T(w.PrecipMmH > 2 ? "Fortes chutes de neige" : "Neige");
            return Loc.T(w.PrecipMmH > 4 ? "Forte pluie" : w.PrecipMmH > 0.5f ? "Pluie" : "Pluie faible");
        }
        if (w.VisibilityM < 1000) return Loc.T("Brouillard");
        float c = w.CloudTotal;
        return Loc.T(c < 0.2f ? "Ciel dégagé" : c < 0.5f ? "Peu nuageux" : c < 0.85f ? "Nuageux" : "Couvert");
    }

    static readonly string[] Dirs = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };
    public static string Cardinal(double fromDeg) => Loc.T(Dirs[(int)Math.Round(((fromDeg % 360) + 360) % 360 / 45) % 8]);

    /// <summary>Bandeau de boussole : graduations tous les 15°, points cardinaux, cap au centre.</summary>
    sealed partial class Compass : Control
    {
        public double Heading; // radians, 0 = nord
        const float Span = 120; // degrés visibles

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            var size = Size;
            float cx = size.X / 2;
            DrawStyleBox(UiTheme.Box(UiTheme.CardBg, 8, 0, 0), new Rect2(Vector2.Zero, size));
            double hd = ((Heading * 180 / Math.PI) % 360 + 360) % 360;
            var font = UiTheme.Bold;
            for (int d = 0; d < 360; d += 15)
            {
                double rel = ((d - hd + 540) % 360) - 180;
                if (Math.Abs(rel) > Span / 2) continue;
                float x = cx + (float)(rel / Span) * size.X;
                float a = 1 - (float)Math.Abs(rel) / (Span / 2);
                if (d % 45 == 0)
                {
                    string t = Loc.T(Dirs[d / 45]);
                    int fs = d % 90 == 0 ? 16 : 13;
                    var col = d == 0 ? new Color(1, 0.45f, 0.4f, a) : new Color(UiTheme.Text, a);
                    var ts = font.GetStringSize(t, HorizontalAlignment.Left, -1, fs);
                    DrawString(font, new Vector2(x - ts.X / 2, size.Y / 2 + fs * 0.36f), t, HorizontalAlignment.Left, -1, fs, col);
                }
                else DrawLine(new Vector2(x, size.Y - 9), new Vector2(x, size.Y - 3), new Color(1, 1, 1, 0.45f * a), 1.5f);
            }
            // repère central et cap
            DrawLine(new Vector2(cx, 0), new Vector2(cx, 5), UiTheme.Accent, 2);
            DrawLine(new Vector2(cx, size.Y - 5), new Vector2(cx, size.Y), UiTheme.Accent, 2);
            string deg = $"{hd:000}°";
            var f2 = UiTheme.Regular;
            var s2 = f2.GetStringSize(deg, HorizontalAlignment.Left, -1, 11);
            DrawString(f2, new Vector2(size.X - s2.X - 8, size.Y / 2 + 4), deg, HorizontalAlignment.Left, -1, 11, new Color(UiTheme.Accent, 0.9f));
        }
    }
}
