using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Menu des réglages graphiques (touche O ou F10) : niveau Bas / Moyen / Élevé / Ultra, résolution 3D,
/// synchronisation verticale et limite d'images par seconde. Les changements s'appliquent tout de suite et sont mémorisés.
/// Au premier lancement, un message indique le niveau choisi automatiquement pour la carte graphique.
/// </summary>
public partial class GraphicsMenu : CanvasLayer
{
    public FlyCamera Camera;

    PanelContainer _panel;
    readonly Button[] _levels = new Button[4];
    Label _detail, _scaleLabel;
    HSlider _scale;
    CheckBox _vsync;
    OptionButton _fps;
    Label _toast;
    double _toastTime;
    bool _syncing;
    bool _wasBlocked;

    static readonly int[] FpsChoices = { 0, 30, 60, 120, 144, 240 };

    public bool IsOpen => _panel.Visible;
    /// <summary>Le menu vient d'être fermé (les Options s'affichent à nouveau s'il a été ouvert depuis elles).</summary>
    public event Action Closed;

    public override void _Ready()
    {
        Layer = 4;
        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.CustomMinimumSize = new Vector2(520, 0);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Both;
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.94f), ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 14, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomRight = 8 });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(box);

        var title = new Label { Text = "Graphismes" };
        title.AddThemeFontSizeOverride("font_size", 24);
        box.AddChild(title);

        var gpu = new Label
        {
            Text = $"Carte graphique : {(GraphicsQuality.GpuName.Length > 0 ? GraphicsQuality.GpuName : "inconnue")}\n" +
                   $"Niveau conseillé : {GraphicsQuality.Name(GraphicsQuality.Recommended)} ({GraphicsQuality.RecommendedReason})",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        gpu.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.88f));
        box.AddChild(gpu);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        var group = new ButtonGroup();
        for (int i = 0; i < 4; i++)
        {
            var l = (QualityLevel)i;
            var b = new Button
            {
                Text = GraphicsQuality.Name(l) + (l == GraphicsQuality.Recommended ? " ★" : ""),
                ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 40),
            };
            b.Pressed += () => { if (!_syncing && GraphicsQuality.Level != l) GraphicsQuality.SetLevel(l); };
            row.AddChild(b);
            _levels[i] = b;
        }

        _detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detail.AddThemeFontSizeOverride("font_size", 14);
        box.AddChild(_detail);

        box.AddChild(new HSeparator());

        var scaleRow = new HBoxContainer();
        scaleRow.AddChild(new Label { Text = "Résolution 3D", CustomMinimumSize = new Vector2(190, 0) });
        _scale = new HSlider { MinValue = 50, MaxValue = 100, Step = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _scale.ValueChanged += _ => { if (!_syncing) ApplyDisplay(); };
        scaleRow.AddChild(_scale);
        _scaleLabel = new Label { CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Right };
        scaleRow.AddChild(_scaleLabel);
        box.AddChild(scaleRow);

        _vsync = new CheckBox { Text = "Synchronisation verticale (pas de déchirure de l'image)", FocusMode = Control.FocusModeEnum.None };
        _vsync.Toggled += _ => { if (!_syncing) ApplyDisplay(); };
        box.AddChild(_vsync);

        var fpsRow = new HBoxContainer();
        fpsRow.AddChild(new Label { Text = "Images par seconde max", CustomMinimumSize = new Vector2(190, 0) });
        _fps = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var f in FpsChoices) _fps.AddItem(f == 0 ? "Sans limite" : f.ToString());
        _fps.ItemSelected += _ => { if (!_syncing) ApplyDisplay(); };
        fpsRow.AddChild(_fps);
        box.AddChild(fpsRow);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var reset = new Button { Text = "Réglage conseillé", FocusMode = Control.FocusModeEnum.None };
        reset.Pressed += () => GraphicsQuality.SetLevel(GraphicsQuality.Recommended);
        buttons.AddChild(reset);
        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "Fermer (O)", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += Toggle;
        buttons.AddChild(close);
        box.AddChild(buttons);

        // Message du premier lancement
        _toast = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _toast.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.KeepSize, 60);
        _toast.GrowHorizontal = Control.GrowDirection.Both;
        _toast.AddThemeFontSizeOverride("font_size", 18);
        _toast.AddThemeColorOverride("font_color", new Color(1, 0.92f, 0.65f));
        _toast.AddThemeColorOverride("font_outline_color", Colors.Black);
        _toast.AddThemeConstantOverride("outline_size", 5);
        AddChild(_toast);
        if (GraphicsQuality.JustDetected)
        {
            _toast.Text = $"Graphismes réglés sur « {GraphicsQuality.Name(GraphicsQuality.Level)} » pour ta machine ({GraphicsQuality.GpuName}). Touche O pour changer.";
            _toast.Visible = true;
            _toastTime = 10;
        }

        GraphicsQuality.Changed += OnChanged;
        Sync();
        // tests automatiques : --graphics ouvre le menu (capture d'écran)
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--graphics") >= 0) CallDeferred(MethodName.Toggle);
    }

    public override void _ExitTree() => GraphicsQuality.Changed -= OnChanged;

    void OnChanged(QualityProfile now, QualityProfile previous) => Sync();

    void ApplyDisplay() =>
        GraphicsQuality.SetDisplay((float)_scale.Value / 100f, _vsync.ButtonPressed, FpsChoices[Math.Max(0, _fps.Selected)]);

    /// <summary>Met les contrôles à jour d'après les réglages actuels (sans déclencher de nouveau changement).</summary>
    void Sync()
    {
        _syncing = true;
        var q = GraphicsQuality.Current;
        for (int i = 0; i < 4; i++) _levels[i].ButtonPressed = i == (int)q.Level;
        _detail.Text = Describe(q);
        _scale.Value = Math.Round(GraphicsQuality.RenderScale * 100);
        _scaleLabel.Text = $"{GraphicsQuality.RenderScale * 100:0} %";
        _vsync.ButtonPressed = GraphicsQuality.VSync;
        _fps.Selected = Math.Max(0, Array.IndexOf(FpsChoices, GraphicsQuality.MaxFps));
        _syncing = false;
    }

    static string Describe(QualityProfile q) => q.Level switch
    {
        QualityLevel.Low => "Pour les cartes graphiques intégrées et les petites machines : relief moins détaillé au loin, forêts clairsemées près du joueur, pas d'ombres, villes et routes affichées moins loin.",
        QualityLevel.Medium => "Pour les cartes graphiques de milieu de gamme : ombres des bâtiments, forêts assez denses, distances d'affichage moyennes.",
        QualityLevel.High => "Pour les cartes graphiques récentes : relief détaillé, forêts complètes, ombres du relief et des arbres, occlusion ambiante.",
        _ => "Pour les machines puissantes : relief très détaillé jusqu'à l'horizon, arbres jusqu'à ~3 km, ombres douces jusqu'à 3 km, anticrénelage 8×, villes et routes visibles plus loin.",
    } + $"\nRelief : finesse ×{q.TerrainSplitFactor:0.##}   arbres : {q.VegetationDensity * 100:0} %   ombres : {(q.Shadows ? $"{q.ShadowMaxDistance:N0} m" : "non")}   bâtiments jusqu'à {q.BuildingsShowDistance / 1000:0} km";

    public void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        if (Camera != null)
        {
            if (_panel.Visible) { _wasBlocked = Camera.InputBlocked; Camera.InputBlocked = true; }
            else Camera.InputBlocked = _wasBlocked;
        }
        if (_panel.Visible) Input.MouseMode = Input.MouseModeEnum.Visible;
        else Closed?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (_toastTime > 0)
        {
            _toastTime -= delta;
            _toast.Modulate = new Color(1, 1, 1, (float)Math.Clamp(_toastTime, 0, 1));
            if (_toastTime <= 0) _toast.Visible = false;
        }
    }

    public override void _Input(InputEvent e)
    {
        // O ou F10 ouvre/ferme, sauf pendant la saisie d'un texte (recherche des menus)
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.PhysicalKeycode != Key.O && k.PhysicalKeycode != Key.F10 && !(IsOpen && k.PhysicalKeycode == Key.Escape)) return;
        if (k.PhysicalKeycode == Key.O && GetViewport().GuiGetFocusOwner() is LineEdit) return;
        Toggle();
        GetViewport().SetInputAsHandled();
    }
}
