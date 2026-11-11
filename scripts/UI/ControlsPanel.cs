using System;
using System.Text.RegularExpressions;
using Godot;

namespace SteamEarth;

/// <summary>
/// Liste des commandes, par thème (F1, ou « Commandes » dans les menus). Les lettres affichées suivent la disposition
/// du clavier du joueur : en AZERTY on lit « Z Q S D », en QWERTY « W A S D » (les touches sont physiques).
/// </summary>
public partial class ControlsPanel : PanelContainer
{
    public event Action Back;

    static readonly (string section, (string keys, string action)[] rows)[] Content =
    {
        ("Se déplacer", new[]
        {
            ("{W} {A} {S} {D} ou ↑ ← ↓ →", "Avancer, aller à gauche, reculer, aller à droite"),
            ("Souris", "Regarder autour de soi (clic pour reprendre la souris)"),
            ("Espace / {E}", "Monter (vol libre)"),
            ("{C} / {Q}", "Descendre (vol libre)"),
            ("Maj", "Aller 8 fois plus vite"),
            ("Molette", "Régler la vitesse de vol"),
            ("1 … 9", "Lieux célèbres : Interlaken, Zermatt, Léman, Europe, Himalaya, New York, Tokyo, Sydney, Rio"),
        }),
        ("À pied", new[]
        {
            ("{F}", "Marcher / revenir au vol libre"),
            ("Espace", "Sauter"),
            ("Maj", "Courir"),
            ("{V}", "Vue arrière / première personne"),
            ("Molette", "Rapprocher ou éloigner la caméra"),
            ("Approcher d'une porte", "Entrer dans un bâtiment (la porte s'ouvre seule) ; marcher dans l'escalier pour changer d'étage"),
            ("Page↑ / Page↓", "Ascenseur (bâtiments de 5 étages et plus)"),
        }),
        ("Véhicules", new[]
        {
            ("{G}", "Choisir un véhicule : voiture, vélo, bateau, avion"),
            ("{E}", "Monter dans le véhicule le plus proche / en descendre"),
            ("{W} / {S} ou ↑ / ↓", "Accélérer / freiner, puis reculer à l'arrêt (avion : piquer / cabrer)"),
            ("{A} / {D}", "Tourner (avion : roulis)"),
            ("Maj / Ctrl", "Avion : plus / moins de gaz"),
            ("← / →", "Tourner (avion : palonnier)"),
            ("Espace", "Avion : freins"),
        }),
        ("Monde", new[]
        {
            ("{M}", "Changer de lieu de départ (ville du monde ou clic sur la Terre)"),
            ("Tab", "Aller dans un pays"),
            ("{K} / clic sur un nom", "Construire les bâtiments d'une ville (Maj : démolir)"),
            ("{H}", "Près d'un arrêt en Suisse : départs des transports publics"),
            ("+ / −", "Avancer / reculer l'heure (Maj : par quart d'heure)"),
            ("{R}", "Revenir à l'heure réelle"),
            ("{J}", "Jour permanent"),
            ("{B}  {T}  {N}  {P}", "Afficher / masquer frontières, routes, noms, transports"),
        }),
        ("Interface", new[]
        {
            ("Échap", "Menu pause"),
            ("Alt", "Libérer la souris (pour cliquer sur un nom)"),
            ("{O}", "Réglages graphiques"),
            ("F1", "Cette aide"),
            ("F2", "Masquer / afficher le HUD"),
            ("F3", "Informations techniques"),
            ("F11", "Plein écran"),
        }),
    };

    /// <summary>Remplace {W}, {A}… par la lettre de la touche physique sur le clavier du joueur.</summary>
    public static string KeyText(string s) => Regex.Replace(Loc.T(s), @"\{(\w+)\}", m =>
    {
        if (!Enum.TryParse<Key>(m.Groups[1].Value, out var k)) return m.Value;
        var kc = DisplayServer.KeyboardGetKeycodeFromPhysical(k);
        var label = OS.GetKeycodeString(kc == Key.None ? k : kc);
        return label.Length > 0 ? label : m.Groups[1].Value;
    });

    VBoxContainer _rows;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(760, 600);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        AddChild(box);
        box.AddChild(UiTheme.Title("Commandes", 28));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rows);
        var back = new Button { Text = "Retour", CustomMinimumSize = new Vector2(140, 40), SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        back.Pressed += () => Back?.Invoke();
        box.AddChild(back);
        Build();
        UiSettings.Changed += Build; // langue
    }

    public override void _ExitTree() => UiSettings.Changed -= Build;

    void Build()
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        foreach (var (section, rows) in Content)
        {
            var s = UiTheme.Section(section);
            s.CustomMinimumSize = new Vector2(0, 30);
            s.VerticalAlignment = VerticalAlignment.Bottom;
            _rows.AddChild(s);
            foreach (var (keys, action) in rows)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 14);
                var k = new Label { Text = KeyText(keys), CustomMinimumSize = new Vector2(210, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled };
                k.AddThemeFontOverride("font", UiTheme.Bold);
                k.AddThemeColorOverride("font_color", UiTheme.Text);
                row.AddChild(k);
                var a = new Label { Text = action, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                a.AddThemeColorOverride("font_color", UiTheme.TextDim);
                row.AddChild(a);
                _rows.AddChild(row);
            }
        }
    }

    public void FocusBack() => GetChild<VBoxContainer>(0).GetChild<Button>(2).GrabFocus();
}
