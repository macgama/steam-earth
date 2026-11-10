using Godot;

namespace SteamEarth;

/// <summary>
/// Style commun de toute l'interface (menus, HUD, panneaux) : panneaux sombres translucides, coins arrondis,
/// une couleur d'accent bleu ciel. Le thème est posé sur la fenêtre racine, donc tous les menus du jeu en héritent.
/// </summary>
public static class UiTheme
{
    public static readonly Color Accent = new(0.40f, 0.78f, 1.00f);
    public static readonly Color Text = new(0.94f, 0.96f, 0.98f);
    public static readonly Color TextDim = new(0.70f, 0.75f, 0.82f);
    public static readonly Color PanelBg = new(0.055f, 0.065f, 0.085f, 0.90f);
    public static readonly Color CardBg = new(0.04f, 0.05f, 0.065f, 0.62f);

    static Theme _theme;
    static Font _regular, _bold;

    public static Font Regular => _regular ??= MakeFont(400);
    public static Font Bold => _bold ??= MakeFont(650);

    /// <summary>Police du système (Segoe UI sous Windows), sinon la police intégrée de Godot.</summary>
    static Font MakeFont(int weight) => new SystemFont
    {
        FontNames = new[] { "Segoe UI", "Inter", "Noto Sans", "Roboto", "Ubuntu", "DejaVu Sans", "Arial", "sans-serif" },
        FontWeight = weight,
        Antialiasing = TextServer.FontAntialiasing.Gray,
        SubpixelPositioning = TextServer.SubpixelPositioning.Auto,
    };

    public static StyleBoxFlat Box(Color bg, int radius = 8, int padX = 16, int padY = 12, Color? border = null, int borderWidth = 0)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            AntiAliasing = true,
        };
        if (border is Color b)
        {
            s.BorderColor = b;
            s.BorderWidthLeft = s.BorderWidthRight = s.BorderWidthTop = s.BorderWidthBottom = borderWidth;
        }
        return s;
    }

    public static Theme Get()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFont = Regular, DefaultFontSize = 16 };

        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, 10, 18, 14));
        t.SetStylebox("panel", "Panel", Box(PanelBg, 10));

        // Boutons : discrets, surlignés à l'accent au survol et au focus (navigation clavier / manette)
        t.SetStylebox("normal", "Button", Box(new Color(1, 1, 1, 0.06f), 6, 14, 7));
        t.SetStylebox("hover", "Button", Box(new Color(Accent, 0.22f), 6, 14, 7));
        t.SetStylebox("pressed", "Button", Box(new Color(Accent, 0.42f), 6, 14, 7));
        t.SetStylebox("hover_pressed", "Button", Box(new Color(Accent, 0.48f), 6, 14, 7));
        t.SetStylebox("disabled", "Button", Box(new Color(1, 1, 1, 0.03f), 6, 14, 7));
        t.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), 6, 14, 7, Accent, 2));
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_pressed_color", "Button", Colors.White);
        t.SetColor("font_hover_pressed_color", "Button", Colors.White);
        t.SetColor("font_focus_color", "Button", Colors.White);
        t.SetColor("font_disabled_color", "Button", new Color(1, 1, 1, 0.35f));

        foreach (var type in new[] { "CheckBox", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(new Color(1, 1, 1, 0.05f), 6, 10, 6));
            t.SetStylebox("hover", type, Box(new Color(Accent, 0.18f), 6, 10, 6));
            t.SetStylebox("pressed", type, Box(new Color(Accent, 0.30f), 6, 10, 6));
            t.SetStylebox("hover_pressed", type, Box(new Color(Accent, 0.30f), 6, 10, 6));
            t.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), 6, 10, 6, Accent, 2));
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Colors.White);
        }

        t.SetStylebox("normal", "LineEdit", Box(new Color(0, 0, 0, 0.35f), 6, 10, 7, new Color(1, 1, 1, 0.12f), 1));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), 6, 10, 7, Accent, 2));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", new Color(1, 1, 1, 0.4f));
        t.SetColor("caret_color", "LineEdit", Accent);

        t.SetColor("font_color", "Label", Text);

        // Curseurs (sensibilité, résolution 3D)
        t.SetStylebox("slider", "HSlider", Box(new Color(1, 1, 1, 0.14f), 3, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(new Color(Accent, 0.75f), 3, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, 3, 0, 3));

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color(1, 1, 1, 0.10f), Thickness = 1 });
        t.SetConstant("separation", "HSeparator", 12);

        // Listes déroulantes
        t.SetStylebox("panel", "PopupMenu", Box(new Color(0.07f, 0.08f, 0.10f, 0.98f), 6, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Box(new Color(Accent, 0.3f), 4, 6, 4));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);

        // Barre de défilement fine
        t.SetStylebox("scroll", "VScrollBar", Box(new Color(1, 1, 1, 0.04f), 3, 3, 0));
        t.SetStylebox("grabber", "VScrollBar", Box(new Color(1, 1, 1, 0.22f), 3, 3, 0));
        t.SetStylebox("grabber_highlight", "VScrollBar", Box(new Color(Accent, 0.6f), 3, 3, 0));
        t.SetStylebox("grabber_pressed", "VScrollBar", Box(Accent, 3, 3, 0));

        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.05f, 0.06f, 0.08f, 0.96f), 6, 10, 6));

        _theme = t;
        return t;
    }

    /// <summary>
    /// Pose le thème sur les contrôles racines de chaque CanvasLayer sous <paramref name="root"/> (le thème de la fenêtre
    /// ne traverse pas les CanvasLayer) et fait de la police du thème la police par défaut (textes 3D compris).
    /// </summary>
    public static void ApplyTo(Node root)
    {
        ThemeDB.FallbackFont = Regular;
        Walk(root, false);
    }

    static void Walk(Node n, bool inLayer)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is CanvasLayer) { Walk(c, true); continue; }
            if (inLayer && c is Control ctl) { ctl.Theme ??= Get(); continue; }
            Walk(c, inLayer);
        }
    }

    /// <summary>Titre d'écran (gras, grand).</summary>
    public static Label Title(string text, int size = 30)
    {
        var l = new Label { Text = text };
        l.AddThemeFontOverride("font", Bold);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    /// <summary>Petit intitulé de section, en majuscules et couleur d'accent.</summary>
    public static Label Section(string text)
    {
        var l = new Label { Text = text, Uppercase = true };
        l.AddThemeFontOverride("font", Bold);
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", Accent);
        return l;
    }

    public static Label Dim(string text, int size = 14)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", TextDim);
        return l;
    }

    /// <summary>Texte lisible par-dessus le monde 3D (contour sombre), pour le HUD.</summary>
    public static Label Overlay(int size = 16, Color? color = null)
    {
        var l = new Label();
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Text);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        l.AddThemeConstantOverride("outline_size", 4);
        return l;
    }

    /// <summary>Grand bouton de menu (écran titre, pause).</summary>
    public static Button MenuButton(string text, System.Action pressed)
    {
        var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(300, 46), FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 19);
        b.AddThemeStyleboxOverride("normal", Box(new Color(1, 1, 1, 0.0f), 6, 18, 8));
        b.AddThemeStyleboxOverride("focus", WithLeftBar(Box(new Color(Accent, 0.16f), 6, 18, 8)));
        b.AddThemeStyleboxOverride("hover", WithLeftBar(Box(new Color(Accent, 0.16f), 6, 18, 8)));
        b.AddThemeStyleboxOverride("pressed", WithLeftBar(Box(new Color(Accent, 0.32f), 6, 18, 8)));
        b.Pressed += pressed;
        b.MouseEntered += () => b.GrabFocus(); // souris et clavier surlignent le même bouton
        return b;
    }

    static StyleBoxFlat WithLeftBar(StyleBoxFlat s)
    {
        s.BorderColor = Accent;
        s.BorderWidthLeft = 3;
        s.BorderWidthRight = s.BorderWidthTop = s.BorderWidthBottom = 0;
        return s;
    }
}
