using System;
using System.Text.RegularExpressions;
using Godot;

namespace SteamEarth;

/// <summary>Niveau de qualité graphique choisi par le joueur (ou détecté au premier lancement).</summary>
public enum QualityLevel { Low = 0, Medium = 1, High = 2, Ultra = 3 }

/// <summary>Qui projette une ombre : permet de couper les ombres des éléments les plus coûteux en premier.</summary>
public enum ShadowCaster { Terrain, Trees, Buildings, Vehicles, Small }

/// <summary>
/// Valeurs concrètes d'un niveau de qualité. Objet immuable : on peut le lire depuis n'importe quel thread
/// (construction des chunks, des routes…) sans verrou. Lire <see cref="GraphicsQuality.Current"/> une fois au début
/// du travail et garder la référence : elle ne change pas pendant qu'on l'utilise.
/// </summary>
public sealed record QualityProfile
{
    public QualityLevel Level { get; init; }

    // ---- Terrain (Planet / ChunkBuilder)
    /// <summary>Plus grand = chunks plus fins plus loin (plus de triangles). 1,6 = réglage historique.</summary>
    public double TerrainSplitFactor { get; init; }
    /// <summary>Niveau max du quadtree dans les zones haute définition (19 ≈ 0,6 m, 18 ≈ 1,2 m, 17 ≈ 2,4 m).</summary>
    public int TerrainHdMaxLevel { get; init; }
    /// <summary>Chunks construits en parallèle (dépend aussi du nombre de cœurs du processeur).</summary>
    public int TerrainBuildThreads { get; init; }

    // ---- Végétation (Vegetation)
    /// <summary>Espacement max des sommets d'un chunk pour y poser des arbres : 11 ≈ ~700 m autour du joueur, 21 ≈ 1,5 km, 42 ≈ 3 km.</summary>
    public double VegetationMaxSpacing { get; init; }
    /// <summary>Fraction des arbres gardés (1 = forêt complète).</summary>
    public float VegetationDensity { get; init; }
    /// <summary>Herbe et cailloux au ras du sol : fraction gardée (0 = pas d'herbe).</summary>
    public float GrassDensity { get; init; }
    /// <summary>Herbe et cailloux visibles jusqu'à (m).</summary>
    public float GrassDistance { get; init; }
    /// <summary>Textures du sol : 0 = couleurs seules, 1 = texture simple, 2 = détail + normal map, 3 = détail complet (mélanges, relief).</summary>
    public int GroundDetail { get; init; }

    // ---- Distances d'affichage
    /// <summary>Facteur général pour les distances d'affichage des nouvelles couches (1 = Élevé).</summary>
    public double DrawDistanceScale { get; init; }
    public double BuildingsShowDistance { get; init; }
    public double RoadsRadiusScale { get; init; }
    public double LabelsRadiusScale { get; init; }

    // ---- Ombres du soleil
    public bool Shadows { get; init; }
    public float ShadowMaxDistance { get; init; }
    public int ShadowAtlasSize { get; init; }
    public DirectionalLight3D.ShadowMode ShadowSplits { get; init; }
    public RenderingServer.ShadowQuality ShadowFilter { get; init; }
    /// <summary>Les éléments qui projettent une ombre (les autres non).</summary>
    public ShadowCaster[] ShadowCasters { get; init; }

    // ---- Image
    public Viewport.Msaa Msaa { get; init; }
    public Viewport.ScreenSpaceAAEnum ScreenSpaceAA { get; init; }
    public bool Ssao { get; init; }
    public bool Glow { get; init; }
    /// <summary>Résolution 3D par défaut de ce niveau (le joueur peut la changer à part).</summary>
    public float DefaultRenderScale { get; init; }

    // ---- Océans et lacs (Ocean, shaders/ocean.gdshader)
    /// <summary>Nombre de vagues de Gerstner additionnées (coût du shader de l'eau proportionnel).</summary>
    public int WaterWaves { get; init; }
    /// <summary>Niveau max du quadtree en mer près de la surface (16 ≈ 5 m, 17 ≈ 2,5 m, 18 ≈ 1,2 m entre sommets).</summary>
    public int WaterMaxLevel { get; init; }

    // ---- Météo
    public int CloudSubdivisions { get; init; }
    public int GlobalCloudSegments { get; init; }
    /// <summary>Multiplie le nombre de gouttes et de flocons.</summary>
    public float PrecipitationFactor { get; init; }

    /// <summary>Choisit une valeur selon le niveau : Pick(bas, moyen, élevé, ultra).</summary>
    public T Pick<T>(T low, T medium, T high, T ultra) => Level switch
    {
        QualityLevel.Low => low,
        QualityLevel.Medium => medium,
        QualityLevel.High => high,
        _ => ultra,
    };

    public bool Casts(ShadowCaster c) => Shadows && Array.IndexOf(ShadowCasters, c) >= 0;
}

/// <summary>
/// Point central de la qualité graphique : niveaux Bas / Moyen / Élevé / Ultra, détection de la carte graphique
/// au premier lancement, réglages mémorisés dans user://settings.cfg (section « graphismes »).
/// Toutes les couches lisent <see cref="Current"/> et s'abonnent à <see cref="Changed"/>. Voir docs/QUALITE_GRAPHIQUE.md.
/// </summary>
public static class GraphicsQuality
{
    const string SettingsPath = "user://settings.cfg";
    const string Section = "graphismes";

    /// <summary>Réglages actuels (immuables, lisibles depuis tous les threads).</summary>
    public static QualityProfile Current { get; private set; } = Build(QualityLevel.High);
    public static QualityLevel Level => Current.Level;

    /// <summary>Résolution du rendu 3D (0,5 à 1 ; l'interface reste nette).</summary>
    public static float RenderScale { get; private set; } = 1f;
    public static bool VSync { get; private set; } = true;
    /// <summary>Limite d'images par seconde (0 = aucune).</summary>
    public static int MaxFps { get; private set; }

    /// <summary>Vrai si le niveau vient de la détection automatique de ce lancement (premier lancement).</summary>
    public static bool JustDetected { get; private set; }
    /// <summary>Niveau conseillé pour cette machine et pourquoi.</summary>
    public static QualityLevel Recommended { get; private set; } = QualityLevel.High;
    public static string RecommendedReason { get; private set; } = "";
    public static string GpuName { get; private set; } = "";

    /// <summary>
    /// Appelé (thread principal) après chaque changement. <c>previous</c> = anciens réglages, pour ne refaire
    /// que ce qui a changé (ex. reconstruire la végétation seulement si sa densité a changé).
    /// </summary>
    public static event Action<QualityProfile, QualityProfile> Changed;

    /// <summary>Raccourci : GraphicsQuality.Pick(bas, moyen, élevé, ultra) pour le niveau actuel.</summary>
    public static T Pick<T>(T low, T medium, T high, T ultra) => Current.Pick(low, medium, high, ultra);

    /// <summary>Réglage d'ombre à donner à un objet selon son type.</summary>
    public static GeometryInstance3D.ShadowCastingSetting ShadowFor(ShadowCaster c) =>
        Current.Casts(c) ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;

    public static string Name(QualityLevel l) => l switch
    {
        QualityLevel.Low => "Bas",
        QualityLevel.Medium => "Moyen",
        QualityLevel.High => "Élevé",
        _ => "Ultra",
    };

    /// <summary>Valeurs de chaque niveau. Élevé = le rendu historique du prototype.</summary>
    public static QualityProfile Build(QualityLevel l)
    {
        int cores = Math.Max(1, OS.GetProcessorCount());
        T P<T>(T a, T b, T c, T d) => l switch { QualityLevel.Low => a, QualityLevel.Medium => b, QualityLevel.High => c, _ => d };
        return new QualityProfile
        {
            Level = l,
            TerrainSplitFactor = P(1.15, 1.35, 1.6, 2.0),
            TerrainHdMaxLevel = P(17, 18, 19, 19),
            TerrainBuildThreads = Math.Clamp(cores - 2, 2, P(3, 4, 6, 10)),
            VegetationMaxSpacing = P(11.0, 21.0, 21.0, 42.0),
            VegetationDensity = P(0.4f, 0.7f, 1f, 1f),
            GrassDensity = P(0f, 0.35f, 0.7f, 1f),
            GrassDistance = P(0f, 40f, 80f, 150f),
            GroundDetail = P(1, 2, 3, 3),
            DrawDistanceScale = P(0.5, 0.75, 1.0, 1.4),
            BuildingsShowDistance = P(10_000.0, 20_000.0, 40_000.0, 60_000.0),
            RoadsRadiusScale = P(0.6, 0.8, 1.0, 1.3),
            LabelsRadiusScale = P(0.7, 0.85, 1.0, 1.2),
            Shadows = l >= QualityLevel.Medium,
            ShadowMaxDistance = P(0f, 300f, 1_000f, 3_000f),
            ShadowAtlasSize = P(1024, 2048, 4096, 8192),
            ShadowSplits = P(DirectionalLight3D.ShadowMode.Orthogonal, DirectionalLight3D.ShadowMode.Parallel2Splits,
                             DirectionalLight3D.ShadowMode.Parallel4Splits, DirectionalLight3D.ShadowMode.Parallel4Splits),
            ShadowFilter = P(RenderingServer.ShadowQuality.Hard, RenderingServer.ShadowQuality.SoftVeryLow,
                             RenderingServer.ShadowQuality.SoftLow, RenderingServer.ShadowQuality.SoftHigh),
            ShadowCasters = P(Array.Empty<ShadowCaster>(),
                              new[] { ShadowCaster.Buildings, ShadowCaster.Vehicles },
                              new[] { ShadowCaster.Buildings, ShadowCaster.Vehicles, ShadowCaster.Trees, ShadowCaster.Terrain },
                              new[] { ShadowCaster.Buildings, ShadowCaster.Vehicles, ShadowCaster.Trees, ShadowCaster.Terrain, ShadowCaster.Small }),
            Msaa = P(Viewport.Msaa.Disabled, Viewport.Msaa.Msaa2X, Viewport.Msaa.Msaa4X, Viewport.Msaa.Msaa8X),
            ScreenSpaceAA = P(Viewport.ScreenSpaceAAEnum.Fxaa, Viewport.ScreenSpaceAAEnum.Disabled, Viewport.ScreenSpaceAAEnum.Disabled, Viewport.ScreenSpaceAAEnum.Disabled),
            Ssao = l >= QualityLevel.High,
            Glow = l >= QualityLevel.Medium,
            DefaultRenderScale = P(0.8f, 1f, 1f, 1f),
            WaterWaves = P(16, 32, 48, 64),
            WaterMaxLevel = P(16, 16, 17, 18),
            CloudSubdivisions = P(16, 32, 48, 96),
            GlobalCloudSegments = P(96, 128, 192, 256),
            PrecipitationFactor = P(0.3f, 0.6f, 1f, 1.5f),
        };
    }

    // ------------------------------------------------------------------ changement et sauvegarde

    /// <summary>Change de niveau (menu, ou toute autre couche) : applique, prévient les couches, mémorise.</summary>
    public static void SetLevel(QualityLevel l, bool resetRenderScale = true)
    {
        var previous = Current;
        Current = Build(l);
        if (resetRenderScale) RenderScale = Current.DefaultRenderScale;
        Save();
        Changed?.Invoke(Current, previous);
    }

    public static void SetDisplay(float renderScale, bool vsync, int maxFps)
    {
        RenderScale = Math.Clamp(renderScale, 0.5f, 1f);
        VSync = vsync;
        MaxFps = Math.Max(0, maxFps);
        Save();
        Changed?.Invoke(Current, Current);
    }

    /// <summary>Lit les réglages ; au premier lancement, détecte la carte graphique et choisit le niveau. À appeler une fois au démarrage.</summary>
    public static void Init()
    {
        Detect();
        var cfg = new ConfigFile();
        bool has = cfg.Load(SettingsPath) == Error.Ok && cfg.HasSectionKey(Section, "niveau");
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--quality=") && int.TryParse(a.Substring(10), out int q))
            {
                // tests automatiques : --quality=0..3, sans toucher aux réglages du joueur
                Current = Build((QualityLevel)Math.Clamp(q, 0, 3));
                RenderScale = Current.DefaultRenderScale;
                GD.Print($"[Graphismes] niveau imposé : {Name(Current.Level)}");
                return;
            }
        if (has)
        {
            Current = Build((QualityLevel)Math.Clamp((int)cfg.GetValue(Section, "niveau", 2), 0, 3));
            RenderScale = (float)cfg.GetValue(Section, "resolution_3d", Current.DefaultRenderScale);
            VSync = (bool)cfg.GetValue(Section, "vsync", true);
            MaxFps = (int)cfg.GetValue(Section, "fps_max", 0);
        }
        else
        {
            Current = Build(Recommended);
            RenderScale = Current.DefaultRenderScale;
            JustDetected = true;
            Save();
        }
        GD.Print($"[Graphismes] {Name(Current.Level)} (conseillé : {Name(Recommended)} — {RecommendedReason})");
    }

    static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // garde les autres réglages (jour permanent…)
        cfg.SetValue(Section, "niveau", (int)Current.Level);
        cfg.SetValue(Section, "resolution_3d", RenderScale);
        cfg.SetValue(Section, "vsync", VSync);
        cfg.SetValue(Section, "fps_max", MaxFps);
        cfg.Save(SettingsPath);
    }

    // ------------------------------------------------------------------ détection de la machine

    /// <summary>Niveau conseillé d'après la carte graphique, la mémoire et le processeur.</summary>
    public static void Detect()
    {
        string name = RenderingServer.GetVideoAdapterName() ?? "";
        string vendor = RenderingServer.GetVideoAdapterVendor() ?? "";
        var type = RenderingServer.GetVideoAdapterType();
        string method = RenderingServer.GetCurrentRenderingMethod() ?? "";
        GpuName = name.Length > 0 ? name : vendor;
        int cores = OS.GetProcessorCount();
        double ramGb = 0;
        try { ramGb = (long)OS.GetMemoryInfo()["physical"] / 1073741824.0; } catch { }

        var (level, why) = Classify(name, type, method);
        if (level > QualityLevel.Medium && ramGb > 0 && ramGb < 7.5) { level = QualityLevel.Medium; why += $", seulement {ramGb:0} Go de mémoire"; }
        if (level > QualityLevel.Medium && cores < 4) { level = QualityLevel.Medium; why += $", {cores} cœurs"; }
        Recommended = level;
        RecommendedReason = why;
    }

    static bool Has(string s, string pattern) => Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase);

    /// <summary>Classe une carte graphique d'après son nom (les plus courantes sur Steam).</summary>
    public static (QualityLevel, string) Classify(string name, RenderingDevice.DeviceType type, string method)
    {
        string n = name;
        if (method == "gl_compatibility") return (QualityLevel.Low, "mode compatibilité OpenGL");
        if (type == RenderingDevice.DeviceType.Cpu || Has(n, "llvmpipe|swiftshader|software|microsoft basic"))
            return (QualityLevel.Low, "rendu logiciel");

        // NVIDIA
        if (Has(n, @"RTX\s*50[789]0|RTX\s*40[789]0|RTX\s*30[89]0|RTX\s*A[456]000|RTX\s*[56]000")) return (QualityLevel.Ultra, "carte graphique haut de gamme");
        if (Has(n, @"RTX\s*(20\d0|30[67]0|40[56]0|50[56]0)|TITAN")) return (QualityLevel.High, "carte graphique récente");
        if (Has(n, @"GTX\s*(10[35]0|9[56]0|[67]\d0)|GT\s*\d{3,4}|Quadro\s*[KP]\d{3}\b")) return (QualityLevel.Low, "carte graphique ancienne ou d'entrée de gamme");
        if (Has(n, @"RTX\s*30[45]0|GTX\s*16\d0|GTX\s*10[678]0|GTX\s*9[78]0|MX\s*[45]\d0")) return (QualityLevel.Medium, "carte graphique milieu de gamme");
        // AMD
        if (Has(n, @"RX\s*(79\d0|78\d0|90[67]0|69[05]0)")) return (QualityLevel.Ultra, "carte graphique haut de gamme");
        if (Has(n, @"RX\s*(6[6-8]\d0|7[67]\d0|9060)")) return (QualityLevel.High, "carte graphique récente");
        if (Has(n, @"RX\s*(5[5-7]\d0|6[45]\d0|5[789]0|Vega)|Radeon\s*(780M|880M|890M|8060S)")) return (QualityLevel.Medium, "carte graphique milieu de gamme");
        // Intel
        if (Has(n, @"Arc.*(A7\d0|B5\d0|B7\d0)")) return (QualityLevel.High, "carte graphique récente");
        if (Has(n, @"Arc")) return (QualityLevel.Medium, "carte graphique Intel Arc");
        if (Has(n, @"UHD|Iris|HD Graphics|Intel\(R\) Graphics")) return (QualityLevel.Low, "carte graphique intégrée");
        // Apple
        if (Has(n, @"Apple M\d+ (Max|Ultra)")) return (QualityLevel.High, "puce Apple haut de gamme");
        if (Has(n, @"Apple M\d+ Pro")) return (QualityLevel.Medium, "puce Apple Pro");
        if (Has(n, @"Apple M\d")) return (QualityLevel.Low, "puce Apple");
        // Mobiles, Steam Deck, puces intégrées inconnues
        if (Has(n, @"Mali|Adreno|PowerVR|Van Gogh|Custom GPU 0405|Radeon\(TM\) Graphics|Radeon Graphics")) return (QualityLevel.Low, "carte graphique intégrée");

        return type switch
        {
            RenderingDevice.DeviceType.DiscreteGpu => (QualityLevel.Medium, "carte graphique dédiée non reconnue"),
            RenderingDevice.DeviceType.IntegratedGpu => (QualityLevel.Low, "carte graphique intégrée"),
            _ => (QualityLevel.Medium, "carte graphique non reconnue"),
        };
    }
}
