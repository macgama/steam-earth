using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Réglages de l'interface et des contrôles (menu Options), mémorisés dans user://settings.cfg, section « interface ».
/// Langue, taille de l'interface, plein écran, HUD, boussole, unités, sensibilité de la souris, axe vertical inversé.
/// </summary>
public static class UiSettings
{
    const string SettingsPath = "user://settings.cfg";
    const string Section = "interface";

    public static string Language { get; private set; } = "fr";
    public static float UiScale { get; private set; } = 1f;
    public static bool Fullscreen { get; private set; }
    public static bool ShowHud { get; private set; } = true;
    public static bool ShowCompass { get; private set; } = true;
    public static bool ShowDebug { get; private set; }
    public static bool Imperial { get; private set; }
    public static float MouseSensitivity { get; private set; } = 1f;
    public static bool InvertY { get; private set; }

    public static readonly float[] UiScales = { 0.8f, 1f, 1.25f, 1.5f, 2f };

    /// <summary>Appelé après chaque changement (les menus et le HUD se mettent à jour).</summary>
    public static event Action Changed;

    static Window _root;

    public static void Init(Window root)
    {
        _root = root;
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) == Error.Ok)
        {
            Language = (string)cfg.GetValue(Section, "langue", DefaultLanguage());
            UiScale = (float)cfg.GetValue(Section, "echelle", 1f);
            Fullscreen = (bool)cfg.GetValue(Section, "plein_ecran", false);
            ShowHud = (bool)cfg.GetValue(Section, "hud", true);
            ShowCompass = (bool)cfg.GetValue(Section, "boussole", true);
            ShowDebug = (bool)cfg.GetValue(Section, "debug", false);
            Imperial = (bool)cfg.GetValue(Section, "imperial", false);
            MouseSensitivity = (float)cfg.GetValue(Section, "sensibilite", 1f);
            InvertY = (bool)cfg.GetValue(Section, "inverser_y", false);
        }
        else Language = DefaultLanguage();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--lang=")) Language = a.Substring(7); // tests : --lang=en
            if (a == "--debug") ShowDebug = true;
        }
        Apply();
    }

    /// <summary>Au premier lancement : la langue du système si elle est traduite, sinon le français.</summary>
    static string DefaultLanguage()
    {
        string sys = OS.GetLocaleLanguage();
        return Array.IndexOf(Loc.Codes, sys) >= 0 ? sys : "fr";
    }

    static void Apply()
    {
        Loc.SetLanguage(Language);
        if (_root != null) _root.ContentScaleFactor = UiScale;
        if (DisplayServer.GetName() != "headless")
        {
            var mode = DisplayServer.WindowGetMode();
            bool fs = mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
            if (Fullscreen != fs) DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        }
        FlyCamera.MouseSensitivity = MouseSensitivity;
        FlyCamera.InvertY = InvertY;
    }

    static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // garde les autres réglages (graphismes, jour permanent…)
        cfg.SetValue(Section, "langue", Language);
        cfg.SetValue(Section, "echelle", UiScale);
        cfg.SetValue(Section, "plein_ecran", Fullscreen);
        cfg.SetValue(Section, "hud", ShowHud);
        cfg.SetValue(Section, "boussole", ShowCompass);
        cfg.SetValue(Section, "debug", ShowDebug);
        cfg.SetValue(Section, "imperial", Imperial);
        cfg.SetValue(Section, "sensibilite", MouseSensitivity);
        cfg.SetValue(Section, "inverser_y", InvertY);
        cfg.Save(SettingsPath);
    }

    static void Set(Action change)
    {
        change();
        Apply();
        Save();
        Changed?.Invoke();
    }

    public static void SetLanguage(string code) => Set(() => Language = code);
    public static void SetUiScale(float s) => Set(() => UiScale = Math.Clamp(s, 0.5f, 3f));
    public static void SetFullscreen(bool on) => Set(() => Fullscreen = on);
    public static void ToggleFullscreen() => SetFullscreen(!Fullscreen);
    public static void SetShowHud(bool on) => Set(() => ShowHud = on);
    public static void SetShowCompass(bool on) => Set(() => ShowCompass = on);
    public static void SetShowDebug(bool on) => Set(() => ShowDebug = on);
    public static void SetImperial(bool on) => Set(() => Imperial = on);
    public static void SetMouseSensitivity(float s) => Set(() => MouseSensitivity = Math.Clamp(s, 0.1f, 5f));
    public static void SetInvertY(bool on) => Set(() => InvertY = on);

    // ------------------------------------------------------------------ unités

    // nombres au format de la langue choisie (« 1 234 » en français, « 1,234 » en anglais)
    static string N0(double v) => v.ToString("N0", Loc.Culture);
    public static string Speed(double ms) => SpeedValue(ms) + " " + SpeedUnit;
    public static string SpeedValue(double ms) => N0(Imperial ? ms * 2.23694 : ms * 3.6);
    public static string SpeedUnit => Imperial ? "mph" : "km/h";
    public static string Length(double m) => Imperial ? N0(m * 3.28084) + " ft" : N0(m) + " m";
    public static string Temperature(double c) => Imperial ? $"{Math.Round(c * 9 / 5 + 32):0} °F" : $"{Math.Round(c):0} °C";
}
