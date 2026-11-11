using System;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>
/// Menu « Personnage » (écran titre, menu pause ou touche U) : choisir qui on incarne et l'habiller.
/// À gauche les réglages, à droite l'aperçu 3D qui tourne lentement. « Valider » enregistre le choix
/// (user://settings.cfg) et habille aussitôt le personnage du jeu et les conducteurs des véhicules.
/// </summary>
public partial class CharacterMenu : PanelContainer
{
    public event Action Back;

    CharacterAppearance _edit;
    CharacterModel _preview;
    Node3D _turntable;
    VBoxContainer _form;
    double _spin;
    string _previewAnim = "Idle";
    CharacterMotion _motion = new() { Grounded = true };
    Button _ok;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(1060, 640);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        AddChild(row);

        // --- réglages (à gauche, défilants)
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(500, 0) };
        left.AddThemeConstantOverride("separation", 8);
        row.AddChild(left);
        left.AddChild(UiTheme.Title("Personnage", 28));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        left.AddChild(scroll);
        _form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _form.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_form);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        left.AddChild(buttons);
        var cancel = new Button { Text = "Annuler", CustomMinimumSize = new Vector2(140, 40) };
        cancel.Pressed += () => Back?.Invoke();
        buttons.AddChild(cancel);
        _ok = new Button { Text = "Valider", CustomMinimumSize = new Vector2(140, 40) };
        _ok.Pressed += () => { PlayerAppearance.Set(_edit); Back?.Invoke(); };
        buttons.AddChild(_ok);

        // --- aperçu 3D (à droite)
        var frame = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(500, 600), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(frame);
        var vp = new SubViewport { OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa4X, Size = new Vector2I(500, 600) };
        frame.AddChild(vp);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.11f, 0.14f, 0.18f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.75f, 0.8f, 0.9f),
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        vp.AddChild(new WorldEnvironment { Environment = env });
        vp.AddChild(new DirectionalLight3D { LightEnergy = 1.4f, ShadowEnabled = true, Rotation = new Vector3(-0.7f, 0.5f, 0) });
        vp.AddChild(new DirectionalLight3D { LightEnergy = 0.5f, LightColor = new Color(0.6f, 0.75f, 1f), Rotation = new Vector3(-0.3f, 2.8f, 0) }); // contre-jour
        var floor = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.8f, Height = 0.04f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.26f, 0.3f), Roughness = 0.9f },
            Position = new Vector3(0, -0.02f, 0),
        };
        vp.AddChild(floor);
        _turntable = new Node3D();
        vp.AddChild(_turntable);
        _preview = new CharacterModel { Name = "Preview" };
        _turntable.AddChild(_preview);
        var cam = new Camera3D { Fov = 30, Position = new Vector3(0, 1.05f, -4.1f) };
        vp.AddChild(cam);
        cam.LookAt(new Vector3(0, 0.95f, 0));

        // gestes de l'aperçu
        var poses = new HBoxContainer();
        poses.AddThemeConstantOverride("separation", 4);
        foreach (var (label, anim) in new[] { ("Repos", "Idle"), ("Marche", "Walk"), ("Course", "Jog_Fwd"), ("Salut", "Yes"), ("Danse", "Dance") })
        {
            var b = new Button { Text = label };
            b.Pressed += () => { _previewAnim = anim; ApplyPose(); };
            poses.AddChild(b);
        }
        left.AddChild(poses);
    }

    /// <summary>Ouvre le menu sur l'apparence actuelle du joueur.</summary>
    public void Open()
    {
        _edit = PlayerAppearance.Current.Clone();
        BuildForm();
        Refresh();
        _ok.CallDeferred(Control.MethodName.GrabFocus);
    }

    void Refresh()
    {
        if (_preview == null || _edit == null) return;
        _preview.SetAppearance(_edit, _edit.AutoOutfit ? Garments.GetOutfit(_edit.Outfit).Mode : GameMode.Explore);
        ApplyPose();
    }

    void ApplyPose()
    {
        _motion = new CharacterMotion { Grounded = true, Speed = _previewAnim switch { "Walk" => 1.4, "Jog_Fwd" => 3.6, _ => 0 } };
        if (_previewAnim is "Yes" or "Dance") _preview.PlayAction(_previewAnim);
    }

    public override void _Process(double delta)
    {
        if (!Visible || _preview == null) return;
        _spin += delta * 0.35;
        _turntable.Rotation = new Vector3(0, (float)(Math.Sin(_spin) * 0.9), 0);
        _preview.Update(_motion, delta);
        if (_previewAnim is "Yes" or "Dance" && _preview.CurrentAnimation is "Idle") _preview.PlayAction(_previewAnim);
    }

    // ---------------------------------------------------------------- formulaire

    void BuildForm()
    {
        foreach (var c in _form.GetChildren()) c.QueueFree();

        _form.AddChild(UiTheme.Section("Personnages"));
        var presets = new HFlowContainer();
        foreach (var p in CharacterAppearance.Presets())
        {
            var b = new Button { Text = p.Name, CustomMinimumSize = new Vector2(76, 34) };
            b.Pressed += () => { _edit = p.Clone(); BuildForm(); Refresh(); };
            presets.AddChild(b);
        }
        _form.AddChild(presets);

        _form.AddChild(UiTheme.Section("Identité"));
        var name = new LineEdit { Text = _edit.Name, MaxLength = 24, PlaceholderText = "Prénom" };
        name.TextChanged += t => _edit.Name = t;
        Row("Prénom", name);
        Row("Corps", Choice(new[] { "Homme", "Femme" }, (int)_edit.Body, i =>
        {
            _edit.Body = (BodyType)i;
            // coiffure par défaut adaptée
            if (i == 1 && _edit.Hair is "short" or "buzzed") _edit.Hair = "long";
            if (i == 0 && _edit.Hair is "long" or "buns" or "buzzed_f") _edit.Hair = "short";
            if (i == 1) _edit.Beard = false;
            BuildForm(); Refresh();
        }));
        Row("Taille", Slider(0.9f, 1.1f, _edit.Height, v => { _edit.Height = v; Refresh(); }, v => $"{(_edit.Body == BodyType.Male ? 1.80f : 1.72f) * v:0.00} m"));
        Row("Corpulence", Slider(0.8f, 1f, _edit.Build, v => { _edit.Build = v; Refresh(); }, v => v < 0.85f ? "Mince" : v < 0.93f ? "Normale" : "Athlétique"));

        _form.AddChild(UiTheme.Section("Visage et cheveux"));
        Row("Peau", Swatches(CharacterAppearance.SkinTones.Select(t => t.Color).ToArray(), c => { _edit.Skin = c; Refresh(); }, tint: new Color(0.93f, 0.72f, 0.58f)));
        var hairs = CharacterAssets.Hairs;
        Row("Coiffure", Choice(hairs.Select(h => h.Name).ToArray(), Array.FindIndex(hairs, h => h.Id == _edit.Hair), i => { _edit.Hair = hairs[i].Id; Refresh(); }));
        Row("Couleur", Swatches(CharacterAppearance.HairColors.Select(t => t.Color).ToArray(), c => { _edit.HairColor = c; Refresh(); }, tint: new Color(0.45f, 0.35f, 0.28f)));
        if (_edit.Body == BodyType.Male)
        {
            var beard = new CheckBox { Text = "Barbe", ButtonPressed = _edit.Beard };
            beard.Toggled += on => { _edit.Beard = on; Refresh(); };
            Row("", beard);
        }

        _form.AddChild(UiTheme.Section("Tenue"));
        var outfits = Garments.Outfits;
        Row("Tenue", Choice(outfits.Select(o => o.Name).ToArray(), Array.FindIndex(outfits, o => o.Id == _edit.Outfit), i =>
        {
            _edit.Outfit = outfits[i].Id;
            _edit.Colors.Clear();
            BuildForm(); Refresh();
        }));
        foreach (var piece in Garments.GetOutfit(_edit.Outfit).Pieces)
        {
            var def = Garments.Get(piece.Id);
            if (def == null) continue;
            var color = _edit.Colors.TryGetValue(def.Slot, out var c) ? c : piece.Color;
            var pick = new ColorPickerButton { Color = color, EditAlpha = false, CustomMinimumSize = new Vector2(120, 30) };
            pick.ColorChanged += nc => { _edit.Colors[def.Slot] = nc; Refresh(); };
            Row(def.Name, pick);
        }
        var auto = new CheckBox { Text = "Tenue auto selon l'activité (vélo, avion, bateau, nage)", ButtonPressed = _edit.AutoOutfit };
        auto.Toggled += on => { _edit.AutoOutfit = on; Refresh(); };
        _form.AddChild(auto);
        _form.AddChild(UiTheme.Dim("Tous les personnages et animations sont libres de droits (CC0, Quaternius). Les vêtements sont générés par le jeu.", 12));
    }

    void Row(string label, Control c)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        var l = UiTheme.Dim(label, 15);
        l.CustomMinimumSize = new Vector2(150, 0);
        l.AutowrapMode = TextServer.AutowrapMode.Off;
        h.AddChild(l);
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(c);
        _form.AddChild(h);
    }

    static OptionButton Choice(string[] items, int selected, Action<int> changed)
    {
        var o = new OptionButton();
        foreach (var it in items) o.AddItem(it);
        o.Selected = Math.Max(0, selected);
        o.ItemSelected += i => changed((int)i);
        return o;
    }

    static Control Slider(float min, float max, float value, Action<float> changed, Func<float, string> text)
    {
        var h = new HBoxContainer();
        var s = new HSlider { MinValue = min, MaxValue = max, Step = 0.01, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 0) };
        var l = UiTheme.Dim(text(value), 14);
        l.CustomMinimumSize = new Vector2(90, 0);
        l.AutowrapMode = TextServer.AutowrapMode.Off;
        s.DragEnded += _ => changed((float)s.Value);
        s.ValueChanged += v => l.Text = text((float)v);
        h.AddChild(s);
        h.AddChild(l);
        return h;
    }

    /// <summary>Pastilles de couleur. <paramref name="tint"/> = couleur de base que les teintes multiplient (aperçu).</summary>
    static Control Swatches(Color[] colors, Action<Color> picked, Color tint)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 6);
        foreach (var c in colors)
        {
            var shown = new Color(Math.Min(1, tint.R * c.R), Math.Min(1, tint.G * c.G), Math.Min(1, tint.B * c.B));
            var b = new Button { CustomMinimumSize = new Vector2(34, 30) };
            b.AddThemeStyleboxOverride("normal", UiTheme.Box(shown, 6, 4, 4));
            b.AddThemeStyleboxOverride("hover", UiTheme.Box(shown, 6, 4, 4, UiTheme.Accent, 2));
            b.AddThemeStyleboxOverride("focus", UiTheme.Box(shown, 6, 4, 4, UiTheme.Accent, 2));
            b.AddThemeStyleboxOverride("pressed", UiTheme.Box(shown, 6, 4, 4, UiTheme.Text, 2));
            b.Pressed += () => picked(c);
            h.AddChild(b);
        }
        return h;
    }
}
