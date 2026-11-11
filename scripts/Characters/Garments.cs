using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>Emplacement d'un vêtement ou d'un accessoire : un seul objet par emplacement.</summary>
public enum GarmentSlot { Top, Outer, Legs, Feet, Hands, Head, Back }

/// <summary>Objet ajouté en plus du vêtement « coque » (modélisé à part, accroché à un os).</summary>
public enum GarmentProp { None, Backpack, CapBrim }

/// <summary>
/// Zone du corps couverte par un vêtement, en coordonnées normalisées du corps au repos (pose en T) :
/// bras 0 = épaule → 1 = poignet (&gt; 1 : main) ; jambe 0 = hanche → 1 = cheville (&gt; 1 : pied) ;
/// tronc 0 = hanche → 1 = base du cou ; tête 0 = base du cou → 1 = sommet du crâne.
/// Une plage vide (min &gt; max) = zone non couverte. Les mêmes valeurs servent aux corps homme et femme.
/// </summary>
public readonly record struct Coverage(
    float TorsoMin = 1, float TorsoMax = 0,
    float ArmMin = 1, float ArmMax = 0,
    float LegMin = 1, float LegMax = 0,
    float HeadMin = 1, float HeadMax = 0)
{
    public bool Torso(float t) => t >= TorsoMin && t <= TorsoMax;
    public bool Arm(float a) => a >= ArmMin && a <= ArmMax;
    public bool Leg(float l) => l >= LegMin && l <= LegMax;
    public bool Head(float h) => h >= HeadMin && h <= HeadMax;
}

/// <summary>
/// Vêtement générique : une « coque » construite sur la peau du corps (même squelette, donc il suit toutes les
/// animations), décollée de <see cref="Thickness"/> mètres. Rien à télécharger : tous les vêtements modernes
/// (t-shirt, jean, doudoune, baskets…) sont générés à partir du corps CC0, avec la couleur choisie.
/// </summary>
public sealed record GarmentDef(
    string Id, string Name, GarmentSlot Slot, Coverage Cover, float Thickness,
    float Roughness = 0.85f, float FabricScale = 40f, float FabricStrength = 0.35f, float Sheen = 0f,
    bool HidesHair = false, GarmentProp Prop = GarmentProp.None, float SoleThickness = 0f, float Quilt = 0f)
{
    /// <summary>Ordre d'empilement : les vêtements du dessus recouvrent ceux du dessous.</summary>
    public int Layer => Slot switch
    {
        GarmentSlot.Legs => 1,
        GarmentSlot.Top => 2,
        GarmentSlot.Hands => 3,
        GarmentSlot.Feet => 4,
        GarmentSlot.Outer => 5,
        GarmentSlot.Head => 6,
        _ => 7,
    };
}

/// <summary>Un vêtement porté, avec sa couleur.</summary>
public readonly record struct Garment(string Id, Color Color);

/// <summary>Activité du joueur : la tenue peut s'adapter (casque à vélo, combinaison de pilote, maillot pour nager…).</summary>
public enum GameMode { Explore, City, Hiking, Winter, Cycling, Driving, Flying, Sailing, Swimming }

/// <summary>Catalogue des vêtements et des tenues complètes (par activité).</summary>
public static class Garments
{
    // Coques : épaisseurs choisies pour l'ordre d'empilement (dessus = plus épais).
    public static readonly GarmentDef[] All =
    {
        // ---- Hauts
        new("tshirt", "T-shirt", GarmentSlot.Top, new Coverage(TorsoMin: -0.04f, TorsoMax: 0.96f, ArmMin: -1, ArmMax: 0.38f, LegMin: -1, LegMax: 0.03f), 0.011f),
        new("longsleeve", "Pull fin", GarmentSlot.Top, new Coverage(TorsoMin: -0.04f, TorsoMax: 0.98f, ArmMin: -1, ArmMax: 0.97f, LegMin: -1, LegMax: 0.03f), 0.012f, FabricScale: 60, FabricStrength: 0.5f),
        new("tank", "Débardeur", GarmentSlot.Top, new Coverage(TorsoMin: -0.04f, TorsoMax: 0.9f, ArmMin: -1, ArmMax: -0.05f, LegMin: -1, LegMax: 0.03f), 0.011f),
        new("jersey", "Maillot de cycliste", GarmentSlot.Top, new Coverage(TorsoMin: -0.04f, TorsoMax: 1.0f, ArmMin: -1, ArmMax: 0.42f, LegMin: -1, LegMax: 0.03f), 0.008f, Roughness: 0.45f, FabricScale: 90, FabricStrength: 0.15f, Sheen: 0.3f),
        new("turtleneck", "Col roulé", GarmentSlot.Top, new Coverage(TorsoMin: -0.04f, TorsoMax: 2, ArmMin: -1, ArmMax: 0.98f, LegMin: -1, LegMax: 0.03f, HeadMin: -1, HeadMax: 0.16f), 0.013f, FabricScale: 30, FabricStrength: 0.7f),
        // ---- Vestes (par-dessus)
        new("jacket", "Veste softshell", GarmentSlot.Outer, new Coverage(TorsoMin: -0.06f, TorsoMax: 2, ArmMin: -1, ArmMax: 0.99f, LegMin: -1, LegMax: 0.04f, HeadMin: -1, HeadMax: 0.08f), 0.017f, Roughness: 0.6f, FabricScale: 25, FabricStrength: 0.25f, Sheen: 0.15f),
        new("down", "Doudoune", GarmentSlot.Outer, new Coverage(TorsoMin: -0.07f, TorsoMax: 2, ArmMin: -1, ArmMax: 0.99f, LegMin: -1, LegMax: 0.05f, HeadMin: -1, HeadMax: 0.12f), 0.032f, Roughness: 0.35f, FabricScale: 8, FabricStrength: 0.4f, Sheen: 0.45f, Quilt: 0.45f),
        new("vest", "Gilet sans manches", GarmentSlot.Outer, new Coverage(TorsoMin: -0.05f, TorsoMax: 1.02f, ArmMin: -1, ArmMax: -0.04f, LegMin: -1, LegMax: 0.03f), 0.02f, Roughness: 0.4f, FabricScale: 8, FabricStrength: 0.4f, Sheen: 0.35f, Quilt: 0.45f),
        new("flight", "Blouson d'aviateur", GarmentSlot.Outer, new Coverage(TorsoMin: 0.0f, TorsoMax: 2, ArmMin: -1, ArmMax: 0.97f, LegMin: -1, LegMax: -0.5f, HeadMin: -1, HeadMax: 0.1f), 0.02f, Roughness: 0.55f, FabricScale: 14, FabricStrength: 0.3f, Sheen: 0.2f),
        new("hivis", "Gilet fluo", GarmentSlot.Outer, new Coverage(TorsoMin: -0.04f, TorsoMax: 0.95f, ArmMin: -1, ArmMax: -0.06f, LegMin: -1, LegMax: 0.03f), 0.014f, Roughness: 0.5f, FabricScale: 70, FabricStrength: 0.2f),
        new("lifevest", "Gilet de sauvetage", GarmentSlot.Outer, new Coverage(TorsoMin: 0.15f, TorsoMax: 1.02f, ArmMin: -1, ArmMax: -0.08f), 0.045f, Roughness: 0.45f, FabricScale: 6, FabricStrength: 0.6f),
        // ---- Bas
        new("jeans", "Jean", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.14f, LegMin: -1, LegMax: 0.97f), 0.007f, Roughness: 0.9f, FabricScale: 70, FabricStrength: 0.55f),
        new("pants", "Pantalon de randonnée", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.14f, LegMin: -1, LegMax: 0.96f), 0.008f, Roughness: 0.75f, FabricScale: 40, FabricStrength: 0.3f),
        new("shorts", "Short", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.13f, LegMin: -1, LegMax: 0.42f), 0.008f),
        new("bike_shorts", "Cuissard", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.16f, LegMin: -1, LegMax: 0.5f), 0.003f, Roughness: 0.4f, FabricScale: 90, FabricStrength: 0.1f, Sheen: 0.3f),
        new("leggings", "Collant", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.15f, LegMin: -1, LegMax: 0.98f), 0.003f, Roughness: 0.5f, FabricScale: 90, FabricStrength: 0.1f, Sheen: 0.2f),
        new("snowpants", "Pantalon de ski", GarmentSlot.Legs, new Coverage(TorsoMin: -2, TorsoMax: 0.2f, LegMin: -1, LegMax: 0.97f), 0.02f, Roughness: 0.4f, FabricScale: 12, FabricStrength: 0.4f, Sheen: 0.3f),
        // ---- Chaussures
        new("sneakers", "Baskets", GarmentSlot.Feet, new Coverage(LegMin: 0.94f, LegMax: 9), 0.014f, Roughness: 0.6f, FabricScale: 50, FabricStrength: 0.3f, SoleThickness: 0.018f),
        new("boots", "Chaussures de marche", GarmentSlot.Feet, new Coverage(LegMin: 0.86f, LegMax: 9), 0.018f, Roughness: 0.7f, FabricScale: 30, FabricStrength: 0.5f, SoleThickness: 0.025f),
        new("snowboots", "Bottes de neige", GarmentSlot.Feet, new Coverage(LegMin: 0.74f, LegMax: 9), 0.026f, Roughness: 0.5f, FabricScale: 10, FabricStrength: 0.5f, SoleThickness: 0.03f),
        new("cycling_shoes", "Chaussures de vélo", GarmentSlot.Feet, new Coverage(LegMin: 0.95f, LegMax: 9), 0.012f, Roughness: 0.35f, FabricScale: 60, FabricStrength: 0.1f, SoleThickness: 0.012f),
        // ---- Mains
        new("gloves", "Gants", GarmentSlot.Hands, new Coverage(ArmMin: 0.93f, ArmMax: 9), 0.006f, Roughness: 0.6f, FabricScale: 40, FabricStrength: 0.4f),
        // ---- Tête
        new("beanie", "Bonnet", GarmentSlot.Head, new Coverage(HeadMin: 0.62f, HeadMax: 9), 0.022f, FabricScale: 25, FabricStrength: 0.9f, HidesHair: true),
        new("cap", "Casquette", GarmentSlot.Head, new Coverage(HeadMin: 0.7f, HeadMax: 9), 0.016f, Roughness: 0.8f, HidesHair: true, Prop: GarmentProp.CapBrim),
        new("helmet", "Casque de vélo", GarmentSlot.Head, new Coverage(HeadMin: 0.66f, HeadMax: 9), 0.04f, Roughness: 0.25f, FabricScale: 1, FabricStrength: 0f, HidesHair: true),
        // ---- Dos
        new("backpack", "Sac à dos", GarmentSlot.Back, new Coverage(), 0f, Roughness: 0.6f, Prop: GarmentProp.Backpack),
    };

    static readonly Dictionary<string, GarmentDef> _byId = All.ToDictionary(g => g.Id);
    public static GarmentDef Get(string id) => id != null && _byId.TryGetValue(id, out var g) ? g : null;

    static Color C(string hex) => new(hex);

    /// <summary>Tenue complète (identifiant, nom affiché, activité, pièces).</summary>
    public sealed record Outfit(string Id, string Name, GameMode Mode, Garment[] Pieces);

    /// <summary>
    /// Tenues prêtes à porter. Chaque activité a sa tenue ; « Tenue auto » (menu Personnage) passe de l'une
    /// à l'autre selon ce que fait le joueur (vélo, avion, nage…).
    /// </summary>
    public static readonly Outfit[] Outfits =
    {
        new("hike", "Randonnée", GameMode.Hiking, new[]
        {
            new Garment("tshirt", C("#e8e2d0")), new Garment("jacket", C("#c4512b")), new Garment("pants", C("#4b5a3c")),
            new Garment("boots", C("#5b4330")), new Garment("backpack", C("#2f5d7c")),
        }),
        new("city", "Ville", GameMode.City, new[]
        {
            new Garment("tshirt", C("#f2f2ee")), new Garment("flight", C("#2c3440")), new Garment("jeans", C("#3a5680")),
            new Garment("sneakers", C("#ececec")),
        }),
        new("casual", "Décontractée", GameMode.Explore, new[]
        {
            new Garment("longsleeve", C("#d9a441")), new Garment("jeans", C("#45628f")), new Garment("sneakers", C("#d23c3c")),
            new Garment("cap", C("#2b2b2b")),
        }),
        new("summer", "Été", GameMode.Explore, new[]
        {
            new Garment("tshirt", C("#3fa7a0")), new Garment("shorts", C("#c9b48a")), new Garment("sneakers", C("#f4f4f4")),
            new Garment("cap", C("#e9e4d4")),
        }),
        new("winter", "Hiver / montagne", GameMode.Winter, new[]
        {
            new Garment("turtleneck", C("#ece6da")), new Garment("down", C("#d8352a")), new Garment("snowpants", C("#23272e")),
            new Garment("snowboots", C("#3a3a3a")), new Garment("gloves", C("#222222")), new Garment("beanie", C("#2d6cb5")),
        }),
        new("cycling", "Cycliste", GameMode.Cycling, new[]
        {
            new Garment("jersey", C("#f4c20d")), new Garment("bike_shorts", C("#1d1d1f")), new Garment("cycling_shoes", C("#f2f2f2")),
            new Garment("helmet", C("#f2f2f2")), new Garment("gloves", C("#1d1d1f")),
        }),
        new("pilot", "Pilote", GameMode.Flying, new[]
        {
            new Garment("longsleeve", C("#e9e3d3")), new Garment("flight", C("#5b4128")), new Garment("pants", C("#3b4351")),
            new Garment("boots", C("#2b2119")),
        }),
        new("sailor", "Navigation", GameMode.Sailing, new[]
        {
            new Garment("longsleeve", C("#f1f1f1")), new Garment("lifevest", C("#f26b1d")), new Garment("shorts", C("#1f3a5f")),
            new Garment("sneakers", C("#1f3a5f")),
        }),
        new("driver", "Conduite", GameMode.Driving, new[]
        {
            new Garment("tshirt", C("#1c1c1c")), new Garment("flight", C("#5a1f25")), new Garment("jeans", C("#2f3f5c")),
            new Garment("sneakers", C("#ececec")),
        }),
        new("swim", "Natation", GameMode.Swimming, Array.Empty<Garment>()),
        new("worker", "Chantier", GameMode.City, new[]
        {
            new Garment("tshirt", C("#4a4a4a")), new Garment("hivis", C("#d8f02a")), new Garment("pants", C("#3d4c63")),
            new Garment("boots", C("#5a3d22")), new Garment("gloves", C("#c9a75b")),
        }),
    };

    public static Outfit GetOutfit(string id) => Outfits.FirstOrDefault(o => o.Id == id) ?? Outfits[0];

    /// <summary>Tenue conseillée pour une activité (tenue auto).</summary>
    public static Outfit ForMode(GameMode mode, string fallback) => mode switch
    {
        GameMode.Cycling => GetOutfit("cycling"),
        GameMode.Flying => GetOutfit("pilot"),
        GameMode.Sailing => GetOutfit("sailor"),
        GameMode.Swimming => GetOutfit("swim"),
        GameMode.Winter => GetOutfit("winter"),
        _ => GetOutfit(fallback),
    };
}
