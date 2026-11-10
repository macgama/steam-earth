using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Écran Options : graphismes (ouvre le menu Graphismes existant), affichage, interface et contrôles.
/// Chaque changement s'applique tout de suite et est mémorisé (<see cref="UiSettings"/>).
/// </summary>
public partial class OptionsMenu : PanelContainer
{
    public event Action Back;
    public event Action OpenGraphics;

    Label _quality;
    CheckBox _fullscreen, _hud, _compass, _debug, _invert;
    OptionButton _scale, _units, _lang;
    HSlider _sens;
    Label _sensLabel;
    Button _back;
    bool _syncing;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(620, 0);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        box.AddChild(UiTheme.Title("Options", 28));

        box.AddChild(UiTheme.Section("Graphismes"));
        var g = Row(box, "Qualité");
        _quality = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        g.AddChild(_quality);
        var gb = new Button { Text = "Réglages graphiques…" };
        gb.Pressed += () => OpenGraphics?.Invoke();
        g.AddChild(gb);

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Section("Affichage"));
        _fullscreen = Check(box, "Plein écran (F11)", on => UiSettings.SetFullscreen(on));
        _scale = Options(Row(box, "Taille de l'interface"), i => UiSettings.SetUiScale(UiSettings.UiScales[i]));
        foreach (var s in UiSettings.UiScales) _scale.AddItem($"{s * 100:0} %");

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Section("Interface"));
        _lang = Options(Row(box, "Langue"), i => UiSettings.SetLanguage(Loc.Codes[i]));
        foreach (var n in Loc.Names) _lang.AddItem(n);
        _units = Options(Row(box, "Unités"), i => UiSettings.SetImperial(i == 1));
        _units.AddItem("Métriques (km/h, m, °C)");
        _units.AddItem("Impériales (mph, ft, °F)");
        _hud = Check(box, "Afficher le HUD (F2)", on => UiSettings.SetShowHud(on));
        _compass = Check(box, "Afficher la boussole", on => UiSettings.SetShowCompass(on));
        _debug = Check(box, "Informations techniques (F3)", on => UiSettings.SetShowDebug(on));

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Section("Contrôles"));
        var sr = Row(box, "Sensibilité de la souris");
        _sens = new HSlider { MinValue = 0.2, MaxValue = 3, Step = 0.05, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _sens.ValueChanged += v => { if (!_syncing) UiSettings.SetMouseSensitivity((float)v); };
        sr.AddChild(_sens);
        _sensLabel = new Label { CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Right, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        sr.AddChild(_sensLabel);
        _invert = Check(box, "Inverser l'axe vertical de la souris", on => UiSettings.SetInvertY(on));

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        _back = new Button { Text = "Retour", CustomMinimumSize = new Vector2(140, 40), SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        _back.Pressed += () => Back?.Invoke();
        box.AddChild(_back);

        UiSettings.Changed += Sync;
        GraphicsQuality.Changed += OnQuality;
        Sync();
    }

    public override void _ExitTree()
    {
        UiSettings.Changed -= Sync;
        GraphicsQuality.Changed -= OnQuality;
    }

    void OnQuality(QualityProfile a, QualityProfile b) => Sync();

    static HBoxContainer Row(VBoxContainer box, string label)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", 10);
        r.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(230, 0) });
        box.AddChild(r);
        return r;
    }

    CheckBox Check(VBoxContainer box, string text, Action<bool> set)
    {
        var c = new CheckBox { Text = text };
        c.Toggled += on => { if (!_syncing) set(on); };
        box.AddChild(c);
        return c;
    }

    OptionButton Options(HBoxContainer row, Action<int> set)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        o.ItemSelected += i => { if (!_syncing) set((int)i); };
        row.AddChild(o);
        return o;
    }

    void Sync()
    {
        _syncing = true;
        _quality.Text = GraphicsQuality.Name(GraphicsQuality.Level);
        _fullscreen.ButtonPressed = UiSettings.Fullscreen;
        int si = Array.IndexOf(UiSettings.UiScales, UiSettings.UiScale);
        _scale.Selected = si >= 0 ? si : 1;
        _lang.Selected = Math.Max(0, Array.IndexOf(Loc.Codes, UiSettings.Language));
        _units.Selected = UiSettings.Imperial ? 1 : 0;
        _hud.ButtonPressed = UiSettings.ShowHud;
        _compass.ButtonPressed = UiSettings.ShowCompass;
        _debug.ButtonPressed = UiSettings.ShowDebug;
        _sens.Value = UiSettings.MouseSensitivity;
        _sensLabel.Text = $"×{UiSettings.MouseSensitivity:0.00}";
        _invert.ButtonPressed = UiSettings.InvertY;
        _syncing = false;
    }

    public void FocusFirst() => _back.GrabFocus();
}
