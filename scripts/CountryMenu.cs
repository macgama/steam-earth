using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Menu des pays (touche Tab ou bouton « Pays ») : liste alphabétique « Pays (Capitale) » avec recherche.
/// Un clic téléporte 500 m au-dessus de la capitale.
/// </summary>
public partial class CountryMenu : CanvasLayer
{
    public FlyCamera Camera;
    PanelContainer _panel;
    LineEdit _search;
    VBoxContainer _list;
    Button _toggle;

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 2; // au-dessus du HUD
        _toggle = new Button { Text = "Pays (Tab)", FocusMode = Control.FocusModeEnum.None };
        _toggle.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _toggle.Position = new Vector2(-150, 10);
        _toggle.Size = new Vector2(140, 34);
        _toggle.Pressed += Toggle;
        AddChild(_toggle);

        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _panel.OffsetLeft = -380; _panel.OffsetRight = -10; _panel.OffsetTop = 52; _panel.OffsetBottom = -10;
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.93f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8, CornerRadiusTopLeft = 6, CornerRadiusBottomLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomRight = 6 });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(box);

        var title = new Label { Text = $"Pays ({Geography.Countries.Count}) — clic : 500 m au-dessus de la capitale" };
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(title);

        _search = new LineEdit { PlaceholderText = "Rechercher un pays ou une capitale…", ClearButtonEnabled = true };
        _search.TextChanged += Filter;
        box.AddChild(_search);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_list);

        foreach (var c in Geography.Countries)
        {
            var country = c;
            var b = new Button { Text = c.ToString(), Alignment = HorizontalAlignment.Left, Flat = true, FocusMode = Control.FocusModeEnum.None };
            b.SetMeta("search", Normalize(c.Name + " " + c.Capital));
            b.Pressed += () => GoTo(country);
            _list.AddChild(b);
        }
    }

    static string Normalize(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    void Filter(string text)
    {
        var q = Normalize(text.Trim());
        foreach (var child in _list.GetChildren())
            if (child is Button b) b.Visible = q.Length == 0 || ((string)b.GetMeta("search")).Contains(q);
    }

    public void Toggle()
    {

        _panel.Visible = !_panel.Visible;
        Camera.InputBlocked = _panel.Visible;
        if (_panel.Visible)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _search.CallDeferred(Control.MethodName.GrabFocus);
        }
        else _search.ReleaseFocus();
    }

    public void GoTo(Country c)
    {
        // 500 m au-dessus de la capitale, reculé de 1,5 km vers le sud pour la voir devant soi
        Camera.SetWalk(false);
        Camera.Teleport(c.Lat, c.Lon, 500, 0, -18);
        var up = Camera.PosD.Normalized();
        Geo.LocalFrame(up, out _, out var north);
        var p = Camera.PosD - north * 1500;
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        Camera.Teleport(lat, lon, 500 + HeightProvider.SurfaceHeight(c.Lat, c.Lon) - HeightProvider.SurfaceHeight(lat, lon), 0, -18);
        GD.Print($"[CountryMenu] {c} → {c.Lat:F4}, {c.Lon:F4}");
        if (_panel.Visible) Toggle();
    }

    public override void _Input(InputEvent e)
    {
        // Tab ouvre/ferme le menu, même quand la recherche a le focus
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.Tab)
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
        else if (IsOpen && e is InputEventKey esc && esc.Pressed && esc.PhysicalKeycode == Key.Escape)
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }
}
