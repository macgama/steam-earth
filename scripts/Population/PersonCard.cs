using Godot;

namespace SteamEarth.Population;

/// <summary>Fiche d'un passant (touche I), en haut à droite, et rappel « I : parler à … » quand on le croise.</summary>
public partial class PersonCard : CanvasLayer
{
    PanelContainer _panel;
    Label _title, _greeting, _body, _footer, _near;
    double _hintTime;

    public override void _Ready()
    {
        Layer = 2;
        _panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelBg, 10, 18, 14, UiTheme.Accent, 1));
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.OffsetRight = -18; _panel.OffsetTop = 70;
        _panel.CustomMinimumSize = new Vector2(430, 0);
        AddChild(_panel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(box);
        _greeting = UiTheme.Dim("", 15);
        _greeting.AddThemeColorOverride("font_color", UiTheme.Accent);
        box.AddChild(_greeting);
        _title = UiTheme.Title("", 22);
        box.AddChild(_title);
        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        _body.AddThemeFontSizeOverride("font_size", 15);
        _body.AddThemeConstantOverride("line_spacing", 4);
        box.AddChild(_body);
        _footer = UiTheme.Dim("", 12);
        _footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_footer);

        _near = UiTheme.Overlay(16);
        _near.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _near.GrowHorizontal = Control.GrowDirection.Both;
        _near.GrowVertical = Control.GrowDirection.Begin;
        _near.OffsetBottom = -150;
        _near.HorizontalAlignment = HorizontalAlignment.Center;
        _near.Visible = false;
        AddChild(_near);
    }

    public void Open(string title, string greeting, string body)
    {
        _title.Text = title;
        _greeting.Text = "« " + greeting + " »";
        _body.Text = body;
        _footer.Text = ControlsPanel.KeyText("Personne imaginaire, tirée des vrais chiffres de son hectare (STATPOP, OFS). {I} : fermer");
        _panel.Visible = true;
        _near.Visible = false;
    }

    public void Close() => _panel.Visible = false;

    /// <summary>Rappel en bas de l'écran (null : rien).</summary>
    public void Near(string text)
    {
        if (_hintTime > 0) return;
        _near.Visible = text != null && !_panel.Visible;
        if (text != null) _near.Text = text;
    }

    /// <summary>Message court (2 s).</summary>
    public void Hint(string text)
    {
        _near.Text = text;
        _near.Visible = true;
        _hintTime = 2;
    }

    public override void _Process(double delta)
    {
        if (_hintTime > 0 && (_hintTime -= delta) <= 0) _near.Visible = false;
    }
}
