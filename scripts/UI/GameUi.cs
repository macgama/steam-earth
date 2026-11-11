using System;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Chef d'orchestre de l'interface : écran titre (la Terre tourne derrière), menu pause (Échap, le jeu est en pause),
/// Options, Commandes, Crédits, et les touches globales (F1 aide, F2 HUD, F3 infos techniques, F11 plein écran,
/// Alt pour libérer la souris).
/// Arguments de test : --pause, --controls, --options, --credits ouvrent l'écran correspondant.
/// </summary>
public partial class GameUi : CanvasLayer
{
    public FlyCamera Camera;
    public StartMenu Start;
    public GraphicsMenu Graphics;
    public CountryMenu Countries;
    public VehicleManager Vehicles;
    public TransitRide Ride;
    public GameHud Hud;

    /// <summary>Nom du jeu affiché (écran titre, barre de la fenêtre).</summary>
    public const string GameName = "Earth Games";

    /// <summary>Un écran de menu (titre, pause, options…) est affiché.</summary>
    public static bool MenuOpen { get; private set; }

    enum Base { None, Title, Pause }
    Base _base = Base.None;
    Control _title, _pause, _dim, _sub;
    VBoxContainer _titleButtons, _pauseButtons;
    ControlsPanel _controls;
    OptionsMenu _options;
    PanelContainer _credits;
    CharacterMenu _character;
    Button _quitTitle, _quitPause;
    bool _startFromTitle, _graphicsFromOptions, _altFreed, _startWasOpen, _played;
    double _orbitLon = 5;

    public override void _Ready()
    {
        Layer = 3; // sous le menu Graphismes (4), qui peut s'ouvrir depuis les Options
        ProcessMode = ProcessModeEnum.Always;
        // Titre de la fenêtre. config/name (project.godot) garde l'ancien nom « Steam Earth » pour l'instant :
        // il fixe le dossier des données (user://), et le changer ferait perdre le cache des tuiles et les réglages.
        GetWindow().Title = GameName;

        // --- écran titre : dégradé sombre à gauche, titre, boutons
        _title = FullRect();
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient { Colors = new[] { new Color(0.02f, 0.03f, 0.05f, 0.92f), new Color(0.02f, 0.03f, 0.05f, 0.55f), new Color(0, 0, 0, 0) }, Offsets = new[] { 0f, 0.55f, 1f } },
                FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 0), Width = 256, Height = 4,
            },
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        shade.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        shade.OffsetRight = 820;
        _title.AddChild(shade);

        var col = new VBoxContainer();
        col.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
        col.OffsetLeft = 90; col.GrowVertical = Control.GrowDirection.Both;
        col.AddThemeConstantOverride("separation", 6);
        _title.AddChild(col);
        var name = UiTheme.Title(GameName.ToUpperInvariant(), 64);
        name.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        name.AddThemeConstantOverride("outline_size", 0);
        col.AddChild(name);
        var tag = UiTheme.Dim("La Terre entière, à l'échelle 1:1", 20);
        tag.AutowrapMode = TextServer.AutowrapMode.Off;
        col.AddChild(tag);
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 36) });
        _titleButtons = new VBoxContainer();
        _titleButtons.AddThemeConstantOverride("separation", 4);
        col.AddChild(_titleButtons);
        _titleButtons.AddChild(UiTheme.MenuButton("Jouer", Play));
        _titleButtons.AddChild(UiTheme.MenuButton("Personnage", OpenCharacter));
        _titleButtons.AddChild(UiTheme.MenuButton("Options", () => OpenSub(_options)));
        _titleButtons.AddChild(UiTheme.MenuButton("Commandes", () => OpenSub(_controls)));
        _titleButtons.AddChild(UiTheme.MenuButton("Crédits", () => OpenSub(_credits)));
        _quitTitle = UiTheme.MenuButton("Quitter", () => Quit(_quitTitle));
        _titleButtons.AddChild(_quitTitle);

        var version = UiTheme.Dim($"{Loc.T("Prototype")} · Godot {Engine.GetVersionInfo()["string"]}", 13);
        version.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        version.AutowrapMode = TextServer.AutowrapMode.Off;
        version.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 18);
        version.GrowVertical = Control.GrowDirection.Begin;
        _title.AddChild(version);

        // --- menu pause
        _pause = FullRect();
        _pause.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var pp = new PanelContainer();
        pp.SetAnchorsPreset(Control.LayoutPreset.Center);
        pp.GrowHorizontal = pp.GrowVertical = Control.GrowDirection.Both;
        _pause.AddChild(pp);
        var pbox = new VBoxContainer();
        pbox.AddThemeConstantOverride("separation", 4);
        pp.AddChild(pbox);
        pbox.AddChild(UiTheme.Title("Pause", 30));
        pbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        _pauseButtons = new VBoxContainer();
        _pauseButtons.AddThemeConstantOverride("separation", 2);
        pbox.AddChild(_pauseButtons);
        _pauseButtons.AddChild(UiTheme.MenuButton("Reprendre", Resume));
        _pauseButtons.AddChild(UiTheme.MenuButton("Changer de lieu", ChangePlace));
        _pauseButtons.AddChild(UiTheme.MenuButton("Personnage", OpenCharacter));
        _pauseButtons.AddChild(UiTheme.MenuButton("Options", () => OpenSub(_options)));
        _pauseButtons.AddChild(UiTheme.MenuButton("Commandes", () => OpenSub(_controls)));
        _pauseButtons.AddChild(UiTheme.MenuButton("Crédits", () => OpenSub(_credits)));
        _pauseButtons.AddChild(UiTheme.MenuButton("Menu principal", BackToTitle));
        _quitPause = UiTheme.MenuButton("Quitter le jeu", () => Quit(_quitPause));
        _pauseButtons.AddChild(_quitPause);

        // --- sous-écrans (sur fond assombri)
        _dim = FullRect();
        _dim.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _dim.AddChild(center);
        _options = new OptionsMenu { Visible = false };
        _options.Back += CloseSub;
        _options.OpenGraphics += () =>
        {
            _options.Visible = false;
            _graphicsFromOptions = true;
            Graphics.Toggle();
        };
        center.AddChild(_options);
        _controls = new ControlsPanel { Visible = false };
        _controls.Back += CloseSub;
        center.AddChild(_controls);
        _credits = BuildCredits();
        center.AddChild(_credits);
        _character = new CharacterMenu { Visible = false };
        _character.Back += CloseSub;
        center.AddChild(_character);

        Graphics.Closed += () =>
        {
            if (!_graphicsFromOptions) return;
            _graphicsFromOptions = false;
            if (_sub == _options) { _options.Visible = true; _options.FocusFirst(); }
        };

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == "--pause") CallDeferred(MethodName.ShowPause);
            if (a == "--controls") CallDeferred(MethodName.OpenSubByName, "controls");
            if (a == "--options") CallDeferred(MethodName.OpenSubByName, "options");
            if (a == "--credits") CallDeferred(MethodName.OpenSubByName, "credits");
            if (a == "--character") CallDeferred(MethodName.OpenSubByName, "character");
        }
        Refresh();
    }

    Control FullRect()
    {
        var c = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(c);
        return c;
    }

    PanelContainer BuildCredits()
    {
        var p = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(700, 0) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        p.AddChild(box);
        box.AddChild(UiTheme.Title("Crédits", 28));
        box.AddChild(UiTheme.Section("Moteur"));
        box.AddChild(UiTheme.Dim("Godot Engine (licence MIT) — godotengine.org", 15));
        box.AddChild(UiTheme.Section("Personnages"));
        box.AddChild(UiTheme.Dim("Corps, coiffures et animations : Quaternius (Universal Base Characters, Universal Animation Library 1 et 2, CC0) — quaternius.com", 15));
        box.AddChild(UiTheme.Section("Données géographiques"));
        foreach (var line in new[]
        {
            Overture.Attribution,
            BuildingsLayer.Attribution,
            SwissBuildings3D.Attribution,
            "Relief : Copernicus DEM GLO-30 © DLR / Airbus, swissALTI3D © swisstopo",
            "Couverture des sols : ESA WorldCover 2021 (CC BY 4.0)",
            Bathymetry.Attribution,
            "Lumières nocturnes : NASA Black Marble",
            "Villes et pays : Natural Earth ; aéroports : OurAirports",
            Timetable.Attribution,
            "Météo et houle : NOAA GFS / WAVEWATCH III",
        })
        {
            var l = UiTheme.Dim("• " + line, 15);
            l.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
            box.AddChild(l);
        }
        box.AddChild(UiTheme.Section("Véhicules"));
        box.AddChild(UiTheme.Dim("Modèles et sons : Kenney (kenney.nl), Starter Kit Racing — CC0", 15));
        var back = new Button { Text = "Retour", CustomMinimumSize = new Vector2(140, 40), SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        back.Pressed += CloseSub;
        box.AddChild(back);
        return p;
    }

    // ------------------------------------------------------------------ écrans

    /// <summary>Écran titre (au lancement, ou « Menu principal » depuis la pause).</summary>
    public void ShowTitle()
    {
        GetTree().Paused = false;
        if (Start.IsOpen) Start.Close();
        if (Camera.Walk || Camera.Vehicle != null) Camera.SetWalk(false);
        // la Terre vue du côté éclairé : on part un peu à l'ouest du point où le soleil est au zénith
        var utc = DateTime.UtcNow;
        _orbitLon = (12 - utc.TimeOfDay.TotalHours) * 15 - 35;
        _base = Base.Title;
        Camera.InputBlocked = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Refresh();
        FocusFirst(_titleButtons);
    }

    public void ShowPause()
    {
        if (_base != Base.None) return;
        _base = Base.Pause;
        GetTree().Paused = true; // le personnage, les véhicules et les voyages s'arrêtent ; le monde continue de se charger
        Camera.InputBlocked = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        ResetQuit();
        Refresh();
        FocusFirst(_pauseButtons);
    }

    public void Resume()
    {
        _base = Base.None;
        GetTree().Paused = false;
        Camera.InputBlocked = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Refresh();
    }

    void Play()
    {
        _base = Base.None;
        _startFromTitle = true;
        Refresh();
        Start.Open(true);
    }

    void ChangePlace()
    {
        _base = Base.None;
        GetTree().Paused = false;
        _startFromTitle = false;
        Refresh();
        Start.Open();
    }

    void BackToTitle()
    {
        Start.SaveNow(); // « Reprendre là où j'étais » au prochain « Jouer »
        _base = Base.None;
        ShowTitle();
    }

    void Quit(Button b)
    {
        // second clic pour confirmer
        if (b.HasMeta("confirm")) { GetTree().Quit(); return; }
        b.SetMeta("confirm", true);
        b.Text = Loc.T("Vraiment quitter ? (cliquer encore)");
    }

    void ResetQuit()
    {
        foreach (var b in new[] { _quitTitle, _quitPause })
        {
            if (b.HasMeta("confirm")) b.RemoveMeta("confirm");
            b.Text = b == _quitTitle ? "Quitter" : "Quitter le jeu";
        }
    }

    void OpenSubByName(string which)
    {
        if (_base == Base.None) ShowPause();
        if (which == "character") { OpenCharacter(); return; }
        OpenSub(which switch { "controls" => _controls, "options" => _options, _ => _credits });
    }

    void OpenCharacter()
    {
        _character.Open();
        OpenSub(_character);
    }

    void OpenSub(Control panel)
    {
        _sub = panel;
        ResetQuit();
        Refresh();
        if (panel == _options) _options.FocusFirst();
        else if (panel == _controls) _controls.FocusBack();
        else if (panel == _character) { }
        else _credits.GetChild(0).GetChild<Button>(-1).GrabFocus();
    }

    void CloseSub()
    {
        _sub = null;
        Refresh();
        FocusFirst(_base == Base.Title ? _titleButtons : _pauseButtons);
    }

    void Refresh()
    {
        _title.Visible = _base == Base.Title && _sub == null;
        _pause.Visible = _base == Base.Pause && _sub == null;
        _dim.Visible = _sub != null;
        _options.Visible = _sub == _options && !_graphicsFromOptions;
        _controls.Visible = _sub == _controls;
        _credits.Visible = _sub == _credits;
        _character.Visible = _sub == _character;
        MenuOpen = _base != Base.None || _sub != null;
    }

    static void FocusFirst(VBoxContainer buttons)
    {
        if (buttons.GetChildCount() > 0) buttons.GetChild<Control>(0).CallDeferred(Control.MethodName.GrabFocus);
    }

    // ------------------------------------------------------------------ chaque image

    public override void _Process(double delta)
    {
        Hud.Hidden = MenuOpen || Start.IsOpen;

        // l'écran titre : la Terre tourne lentement, vue de l'orbite
        if (_base == Base.Title)
        {
            _orbitLon += delta * 0.8;
            Camera.Teleport(22, (_orbitLon + 180) % 360 - 180, 9_000_000, 0, -89.9);
            // la Terre décalée vers la droite, à côté des boutons (décalage de rendu seulement : la position du joueur ne change pas)
            float w = GetViewport().GetVisibleRect().Size.X, h = GetViewport().GetVisibleRect().Size.Y;
            double focal = h / 2 / Math.Tan(Mathf.DegToRad(Camera.Fov / 2));
            Camera.HOffset = -(float)(w * 0.17 / focal * (9_000_000 + Geo.Radius));
        }
        else if (Camera.HOffset != 0) Camera.HOffset = 0;

        // fin du choix du lieu de départ : la partie commence
        if (_startWasOpen && !Start.IsOpen && _base == Base.None)
        {
            if (!_played) Hud.ShowStartHint();
            _played = true;
        }
        _startWasOpen = Start.IsOpen;
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey k)
        {
            if (k.PhysicalKeycode == Key.Alt) { FreeMouse(k.Pressed); return; }
            if (k.Pressed && !k.Echo)
            {
                switch (k.PhysicalKeycode)
                {
                    case Key.F11: UiSettings.ToggleFullscreen(); Handled(); return;
                    case Key.F3: UiSettings.SetShowDebug(!UiSettings.ShowDebug); Handled(); return;
                    case Key.F2 when !MenuOpen: UiSettings.SetShowHud(!UiSettings.ShowHud); Handled(); return;
                    case Key.F1 when !Graphics.IsOpen && !Start.IsOpen:
                        if (_sub == _controls) CloseSub();
                        else { if (_base == Base.None) ShowPause(); OpenSub(_controls); }
                        Handled(); return;
                    case Key.Escape: Escape(); return;
                    case Key.U when !MenuOpen && !Graphics.IsOpen && !Start.IsOpen && !Countries.IsOpen && !Vehicles.IsOpen:
                        ShowPause(); OpenCharacter(); Handled(); return;
                }
            }
            // les menus ouverts gardent les touches pour eux (pas de M, Tab, G… derrière la pause)
            if (MenuOpen && !Graphics.IsOpen && !IsUiAction(e)) Handled();
        }
    }

    void Escape()
    {
        // ces menus se ferment eux-mêmes avec Échap
        if (Graphics.IsOpen || Countries.IsOpen || Vehicles.IsOpen || Ride.BoardOpen) return;
        Handled();
        if (_sub != null) { CloseSub(); return; }
        if (Start.IsOpen)
        {
            if (_startFromTitle) { Start.Close(); ShowTitle(); }
            else Start.Cancel();
            return;
        }
        if (_base == Base.Title) return;
        if (_base == Base.Pause) { Resume(); return; }
        ShowPause();
    }

    /// <summary>Alt maintenu : la souris est libre (cliquer sur un nom de ville), puis reprise au relâchement.</summary>
    void FreeMouse(bool pressed)
    {
        if (MenuOpen || Camera.InputBlocked) return;
        if (pressed && Input.MouseMode == Input.MouseModeEnum.Captured) { Input.MouseMode = Input.MouseModeEnum.Visible; _altFreed = true; }
        else if (!pressed && _altFreed) { Input.MouseMode = Input.MouseModeEnum.Captured; _altFreed = false; }
    }

    static readonly string[] UiActions = { "ui_up", "ui_down", "ui_left", "ui_right", "ui_accept", "ui_select", "ui_focus_next", "ui_focus_prev", "ui_cancel", "ui_page_up", "ui_page_down", "ui_home", "ui_end" };
    static bool IsUiAction(InputEvent e)
    {
        foreach (var a in UiActions) if (e.IsAction(a)) return true;
        return false;
    }

    void Handled() => GetViewport().SetInputAsHandled();
}
