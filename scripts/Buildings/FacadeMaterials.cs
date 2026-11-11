using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Matière d'une surface de bâtiment, dessinée par le shader des bâtiments (<see cref="BuildingShader"/>).
/// Elle voyage dans l'alpha de la couleur des sommets : alpha = 1 − code / 255 (0 = « uni », l'aspect d'avant).
/// Les UV sont en mètres : murs = (abscisse le long du mur, hauteur au-dessus du rez-de-chaussée, 3 unités par étage),
/// toits = (horizontale le long du pan, distance vers le bas de la pente) — voir <see cref="FacadeMaterials.SlopeUv"/>.
/// Les mêmes conventions servent aux modèles 3D importés (swissBUILDINGS3D) : voir docs/MATERIAUX_BATIMENTS.md.
/// </summary>
public enum Surface : byte
{
    Plain = 0,
    // Murs
    Render = 1,        // crépi
    Wood = 2,          // madriers ou planches horizontales (chalet, grange)
    Stone = 3,         // moellons
    Brick = 4,
    Concrete = 5,      // béton banché (panneaux de coffrage)
    Metal = 6,         // bardage métallique ondulé (halles, entrepôts)
    Chalet = 7,        // rez-de-chaussée crépi, étages en bois (la couleur des sommets est celle du bois)
    TimberFrame = 8,   // colombages : crépi clair et poutres sombres
    Siding = 9,        // clins de bois peints (Amérique du Nord, Scandinavie)
    // Toits
    RoofTiles = 16,    // tuiles en terre cuite
    RoofSlate = 17,    // ardoise ou fibrociment
    RoofMetal = 18,    // tôle à joints debout
    RoofShingles = 19, // tavillons (bardeaux de bois), bardeaux d'asphalte selon la couleur
    RoofGravel = 20,   // toit plat gravillonné
    RoofCopper = 21,   // cuivre vert-de-gris
    RoofGreen = 22,    // toit végétalisé
    RoofStone = 23,    // lauzes (dalles de pierre : Tessin, Valais)
    // Balcons
    RailWood = 30,     // balustrade en planches découpées (chalet)
    RailMetal = 31,    // garde-corps à barreaux
    DeckWood = 32,     // plancher de balcon
}

/// <summary>Balcons ajoutés aux façades (Moyen et plus).</summary>
public enum BalconyKind : byte { None, Wood, Metal }

/// <summary>Aspect choisi pour un bâtiment : matières, couleurs, volets, balcons.</summary>
public readonly record struct BuildingLook(Surface Wall, Surface Roof, bool Shutters, BalconyKind Balconies, Color WallColor, Color RoofColor)
{
    /// <summary>Code du mur pour <see cref="MeshAcc.Material"/> (avec le drapeau des volets).</summary>
    public byte WallCode => FacadeMaterials.Code(Wall, Shutters);
    public byte RoofCode => FacadeMaterials.Code(Roof);
}

/// <summary>
/// Choix des matières des façades et des toits quand OSM ne les donne pas : d'après la région (Alpes, Tessin,
/// Europe du Nord-Ouest, Amérique du Nord…), l'altitude du terrain, la fonction du bâtiment et un aléa stable.
/// </summary>
public static class FacadeMaterials
{
    /// <summary>Ajouté au code d'un mur d'habitation : fenêtres à volets.</summary>
    public const byte ShuttersFlag = 64;

    public static byte Code(Surface s, bool shutters = false) => (byte)((byte)s | (shutters ? ShuttersFlag : 0));

    /// <summary>Couleur de sommet portant la matière (alpha codé).</summary>
    public static Color Encode(Color c, byte code) => new(c.R, c.G, c.B, 1f - code / 255f);

    /// <summary>UV en mètres d'une face plane quelconque (pan de toit, face d'un modèle importé) : u horizontal le long
    /// de la face, v vers le bas de la pente. <paramref name="p"/> et <paramref name="n"/> sont dans un repère local
    /// dont z est la verticale. Sur une face verticale, v descend aussi (−altitude).</summary>
    public static Vector2 SlopeUv(double px, double py, double pz, double nx, double ny, double nz)
    {
        double tx = -ny, ty = nx; // horizontale du plan : verticale × normale
        double tl = Math.Sqrt(tx * tx + ty * ty);
        if (tl < 1e-4) return new Vector2((float)px, (float)-py); // face horizontale : plan (est, sud)
        tx /= tl; ty /= tl;
        // ligne de plus grande pente vers le bas : n × t, orientée vers z négatif
        double sx = ny * 0 - nz * ty, sy = nz * tx - nx * 0, sz = nx * ty - ny * tx;
        if (sz > 0) { sx = -sx; sy = -sy; sz = -sz; }
        double sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        return new Vector2((float)(px * tx + py * ty), (float)((px * sx + py * sy + pz * sz) / Math.Max(sl, 1e-9)));
    }

    static double H(double seed, double k)
    {
        double s = Math.Sin(seed * 127.1 + k * 311.7) * 43758.5453;
        return s - Math.Floor(s);
    }

    static double Smooth(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    static Surface? FromOsmWall(string m) => m switch
    {
        null => null,
        "brick" => Surface.Brick,
        "wood" or "timber" => Surface.Wood,
        "stone" or "sandstone" or "granite" or "limestone" => Surface.Stone,
        "concrete" => Surface.Concrete,
        "metal" or "steel" => Surface.Metal,
        "plaster" or "render" or "stucco" or "cement_block" => Surface.Render,
        "timber_framing" or "half-timbered" => Surface.TimberFrame,
        "glass" or "mirror" => Surface.Plain,
        _ => null,
    };

    static Surface? FromOsmRoof(string m) => m switch
    {
        null => null,
        "roof_tiles" or "tile" or "tiles" or "clay" => Surface.RoofTiles,
        "slate" or "eternit" or "fibre_cement" => Surface.RoofSlate,
        "metal" or "metal_sheet" or "tin" or "zinc" or "steel" => Surface.RoofMetal,
        "copper" => Surface.RoofCopper,
        "wood" or "shingle" or "shingles" or "asphalt_shingle" => Surface.RoofShingles,
        "grass" or "plants" or "green" => Surface.RoofGreen,
        "stone" => Surface.RoofStone,
        "gravel" or "concrete" or "tar_paper" or "asphalt" or "bitumen" or "pebbles" => Surface.RoofGravel,
        "glass" => Surface.Plain,
        _ => null,
    };

    enum Region { Other, Alps, Ticino, SwissLowland, NorthWestEurope, Mediterranean, NorthAmerica, Scandinavia }

    static Region RegionOf(double lat, double lon, double alt)
    {
        if (lat > 45.75 && lat < 46.55 && lon > 7.9 && lon < 9.4 && alt > 400) return Region.Ticino;       // Tessin, val d'Ossola, Valais du Sud
        if (lat > 44.0 && lat < 48.2 && lon > 5.5 && lon < 16.5 && alt > 650) return Region.Alps;
        if (lat > 45.8 && lat < 47.9 && lon > 5.9 && lon < 10.6) return Region.SwissLowland;               // Plateau suisse, Jura
        if (lat > 55 && lat < 71 && lon > 4 && lon < 32) return Region.Scandinavia;
        if (lat > 50.3 && lat < 58.8 && lon > -8 && lon < 15) return Region.NorthWestEurope;              // Benelux, nord de l'Allemagne, Danemark, îles Britanniques
        if (lat > 34 && lat < 45 && lon > -10 && lon < 36) return Region.Mediterranean;
        if (lat > 24 && lat < 62 && lon > -130 && lon < -52) return Region.NorthAmerica;
        return Region.Other;
    }

    static readonly Color[] WoodCols = { new(0.42f, 0.27f, 0.16f), new(0.33f, 0.21f, 0.13f), new(0.52f, 0.36f, 0.21f), new(0.38f, 0.25f, 0.15f) };
    static readonly Color[] BarnCols = { new(0.40f, 0.30f, 0.21f), new(0.45f, 0.42f, 0.38f), new(0.36f, 0.25f, 0.17f) };
    static readonly Color[] BrickCols = { new(0.60f, 0.32f, 0.24f), new(0.52f, 0.27f, 0.21f), new(0.66f, 0.44f, 0.31f), new(0.45f, 0.24f, 0.20f) };
    static readonly Color[] StoneCols = { new(0.60f, 0.58f, 0.52f), new(0.55f, 0.56f, 0.48f), new(0.66f, 0.62f, 0.54f), new(0.52f, 0.51f, 0.50f) };
    static readonly Color[] SidingCols = { new(0.90f, 0.90f, 0.87f), new(0.62f, 0.70f, 0.76f), new(0.78f, 0.74f, 0.64f), new(0.60f, 0.62f, 0.60f), new(0.70f, 0.30f, 0.24f), new(0.85f, 0.80f, 0.62f) };
    static readonly Color[] ScandiCols = { new(0.55f, 0.16f, 0.12f), new(0.88f, 0.85f, 0.76f), new(0.82f, 0.68f, 0.35f), new(0.45f, 0.48f, 0.42f) };
    static readonly Color[] MetalCols = { new(0.58f, 0.60f, 0.62f), new(0.44f, 0.50f, 0.56f), new(0.70f, 0.70f, 0.68f), new(0.52f, 0.28f, 0.24f), new(0.30f, 0.38f, 0.36f) };
    static readonly Color[] RenderLight = { new(0.88f, 0.85f, 0.78f), new(0.90f, 0.88f, 0.82f), new(0.86f, 0.80f, 0.68f) };
    static readonly Color[] TileCols = { new(0.58f, 0.30f, 0.22f), new(0.50f, 0.26f, 0.20f), new(0.63f, 0.38f, 0.26f), new(0.44f, 0.24f, 0.19f) };
    static readonly Color[] RoofMetalCols = { new(0.45f, 0.48f, 0.50f), new(0.40f, 0.16f, 0.14f), new(0.25f, 0.26f, 0.28f), new(0.30f, 0.38f, 0.33f) };

    static Color Pick(Color[] cols, double seed, double k) => cols[(int)(H(seed, k) * cols.Length) % cols.Length];

    /// <summary>
    /// Aspect d'un bâtiment. <paramref name="wall"/> et <paramref name="roof"/> sont les couleurs déjà calculées
    /// (OSM ou palettes par fonction) : elles ne sont remplacées que si OSM n'en donnait pas et que la matière choisie
    /// a sa propre teinte (bois, brique, pierre…).
    /// </summary>
    public static BuildingLook Choose(string facadeMaterial, string roofMaterial, bool osmWallColor, bool osmRoofColor,
        BuildingKind kind, WindowStyle style, bool pitched, double lat, double lon, double groundAlt, int levels,
        double footprint, double height, double seed, Color wall, Color roof)
    {
        var region = RegionOf(lat, lon, groundAlt);
        double r = H(seed, 1), r2 = H(seed, 2), r3 = H(seed, 3);
        bool housing = kind is BuildingKind.House or BuildingKind.Apartments or BuildingKind.Generic or BuildingKind.Hotel
            or BuildingKind.Restaurant or BuildingKind.Shop;
        bool small = footprint < 260 && height < 16;

        // ---- murs
        Surface w;
        var osmW = FromOsmWall(facadeMaterial);
        if (osmW != null) w = osmW.Value;
        else if (style is WindowStyle.Glass) w = Surface.Plain;
        else w = kind switch
        {
            BuildingKind.Church or BuildingKind.Chapel or BuildingKind.Cathedral => r < 0.55 ? Surface.Stone : Surface.Render,
            BuildingKind.Mosque or BuildingKind.Temple or BuildingKind.Hospital or BuildingKind.Kindergarten => Surface.Render,
            BuildingKind.TrainStation or BuildingKind.Museum or BuildingKind.Library => r < 0.45 ? Surface.Stone : Surface.Render,
            BuildingKind.Civic => r < 0.35 ? Surface.Stone : r < 0.7 ? Surface.Render : Surface.Concrete,
            BuildingKind.Office => r < 0.5 ? Surface.Concrete : Surface.Render,
            BuildingKind.School or BuildingKind.University => region == Region.NorthWestEurope && r < 0.6 ? Surface.Brick : r < 0.6 ? Surface.Render : Surface.Concrete,
            BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.Supermarket or BuildingKind.SportsHall =>
                r < 0.6 ? Surface.Metal : Surface.Concrete,
            BuildingKind.Stadium or BuildingKind.Grandstand or BuildingKind.Garage or BuildingKind.Tank => Surface.Concrete,
            BuildingKind.Shed => region is Region.NorthAmerica or Region.Scandinavia || r < 0.75 ? Surface.Wood : Surface.Concrete,
            BuildingKind.FireStation => r < 0.5 ? Surface.Brick : Surface.Render,
            BuildingKind.Farm => region switch
            {
                Region.Alps or Region.SwissLowland => r < 0.55 ? Surface.Chalet : r < 0.85 ? Surface.Wood : Surface.Render,
                Region.Ticino => r < 0.6 ? Surface.Stone : Surface.Wood,
                Region.NorthAmerica or Region.Scandinavia => Surface.Wood,
                Region.NorthWestEurope => r < 0.6 ? Surface.Brick : Surface.Wood,
                _ => r < 0.5 ? Surface.Stone : Surface.Render,
            },
            _ when housing => HousingWall(region, groundAlt, kind, small, levels, r, r2),
            _ => Surface.Render,
        };

        // ---- toit
        Surface rf;
        var osmR = FromOsmRoof(roofMaterial);
        if (osmR != null) rf = osmR.Value;
        else if (!pitched) rf = kind is BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.SportsHall && r2 < 0.5 ? Surface.RoofMetal
            : kind is BuildingKind.Greenhouse ? Surface.Plain
            : region is Region.SwissLowland or Region.Alps && r3 < 0.25 && footprint < 3000 ? Surface.RoofGreen : Surface.RoofGravel;
        else if (kind is BuildingKind.Greenhouse) rf = Surface.Plain;
        else
        {
            // d'après la couleur déjà tirée (rouge = tuiles, gris = ardoise, vert = cuivre), puis la région
            bool reddish = roof.R > roof.B + 0.12f;
            bool green = roof.G > roof.R + 0.08f;
            rf = green ? Surface.RoofCopper
                : region switch
                {
                    Region.Alps => w is Surface.Wood or Surface.Chalet
                        ? (r2 < 0.40 ? Surface.RoofSlate : r2 < 0.62 ? Surface.RoofMetal : r2 < 0.80 ? Surface.RoofShingles : Surface.RoofTiles)
                        : (reddish ? Surface.RoofTiles : r2 < 0.6 ? Surface.RoofSlate : Surface.RoofMetal),
                    Region.Ticino => small && r2 < 0.55 ? Surface.RoofStone : reddish ? Surface.RoofTiles : Surface.RoofSlate,
                    Region.NorthAmerica => kind is BuildingKind.Farm or BuildingKind.Shed && r2 < 0.5 ? Surface.RoofMetal : Surface.RoofShingles,
                    Region.Scandinavia => reddish ? Surface.RoofTiles : r2 < 0.6 ? Surface.RoofMetal : Surface.RoofSlate,
                    _ => kind is BuildingKind.Farm && r2 < 0.3 ? Surface.RoofMetal : reddish ? Surface.RoofTiles : Surface.RoofSlate,
                };
        }

        // ---- couleurs propres à la matière (si OSM n'en donne pas)
        if (!osmWallColor)
            wall = w switch
            {
                Surface.Wood => kind is BuildingKind.Farm or BuildingKind.Shed ? Pick(BarnCols, seed, 4) : Pick(WoodCols, seed, 4),
                Surface.Chalet => Pick(WoodCols, seed, 4),
                Surface.Brick => Pick(BrickCols, seed, 4),
                Surface.Stone => kind is BuildingKind.Church or BuildingKind.Chapel or BuildingKind.Cathedral ? wall : Pick(StoneCols, seed, 4),
                Surface.Metal => Pick(MetalCols, seed, 4),
                Surface.Siding => region == Region.Scandinavia ? Pick(ScandiCols, seed, 4) : Pick(SidingCols, seed, 4),
                Surface.TimberFrame => Pick(RenderLight, seed, 4),
                _ => wall,
            };
        if (!osmRoofColor)
            roof = rf switch
            {
                Surface.RoofTiles => roof.R > roof.B + 0.12f ? roof : Pick(TileCols, seed, 5),
                Surface.RoofSlate => region == Region.Alps && r3 < 0.5 ? new Color(0.43f, 0.44f, 0.45f) : new Color(0.29f, 0.30f, 0.33f),
                Surface.RoofMetal => Pick(RoofMetalCols, seed, 5),
                Surface.RoofShingles => region == Region.NorthAmerica
                    ? (r3 < 0.5 ? new Color(0.27f, 0.27f, 0.28f) : new Color(0.36f, 0.31f, 0.27f))
                    : new Color(0.44f, 0.40f, 0.36f), // tavillons gris argenté
                Surface.RoofCopper => new Color(0.36f, 0.55f, 0.47f),
                Surface.RoofGreen => new Color(0.36f, 0.45f, 0.26f),
                Surface.RoofStone => new Color(0.46f, 0.45f, 0.43f),
                _ => roof,
            };

        // ---- volets (fenêtres d'habitation, surtout en Suisse, France, Italie)
        bool residentialWindows = style is WindowStyle.Residential or WindowStyle.ShopResidential;
        double pShutters = region switch
        {
            Region.SwissLowland or Region.Alps or Region.Ticino or Region.Mediterranean => 0.72,
            Region.NorthWestEurope or Region.Scandinavia => 0.12,
            Region.NorthAmerica => 0.35,
            _ => 0.35,
        };
        if (w is Surface.Metal or Surface.Concrete or Surface.Plain) pShutters *= 0.3;
        bool shutters = residentialWindows && levels <= 8 && H(seed, 6) < pShutters;

        // ---- balcons
        var balc = BalconyKind.None;
        if (housing && levels >= 2 && style is WindowStyle.Residential or WindowStyle.ShopResidential)
        {
            double b = H(seed, 7);
            if (w is Surface.Chalet or Surface.Wood) balc = b < 0.8 ? BalconyKind.Wood : BalconyKind.None;
            else if (kind is BuildingKind.Apartments || levels >= 4) balc = levels <= 14 && b < (region == Region.NorthAmerica ? 0.3 : 0.6) ? BalconyKind.Metal : BalconyKind.None;
            else if (b < 0.3) balc = region is Region.Alps or Region.SwissLowland && b < 0.15 ? BalconyKind.Wood : BalconyKind.Metal;
        }

        return new BuildingLook(w, rf, shutters, balc, wall, roof);
    }

    static Surface HousingWall(Region region, double alt, BuildingKind kind, bool small, int levels, double r, double r2)
    {
        switch (region)
        {
            case Region.Alps:
            {
                // plus on monte, plus il y a de chalets en bois
                double chalet = (small ? 0.35 : 0.15) + Smooth(650, 1400, alt) * (small ? 0.5 : 0.35);
                if (kind is BuildingKind.Apartments && levels > 4) chalet *= 0.3;
                if (r < chalet) return r2 < 0.75 ? Surface.Chalet : Surface.Wood;
                return r2 < 0.12 ? Surface.Stone : Surface.Render;
            }
            case Region.Ticino:
                return small && r < 0.4 ? Surface.Stone : r < 0.5 ? Surface.Stone : Surface.Render;
            case Region.SwissLowland:
                if (small && r < 0.10) return Surface.Chalet;
                if (small && r < 0.17) return Surface.TimberFrame;
                return r < 0.25 && levels <= 5 ? Surface.Stone : Surface.Render;
            case Region.NorthWestEurope:
                return r < 0.62 ? Surface.Brick : r < 0.7 && small ? Surface.TimberFrame : Surface.Render;
            case Region.Scandinavia:
                return small && r < 0.7 ? Surface.Siding : r < 0.4 ? Surface.Brick : Surface.Render;
            case Region.NorthAmerica:
                return small ? (r < 0.6 ? Surface.Siding : r < 0.85 ? Surface.Brick : Surface.Render) : (r < 0.45 ? Surface.Brick : Surface.Render);
            case Region.Mediterranean:
                return r < 0.15 ? Surface.Stone : Surface.Render;
            default:
                return r < 0.15 ? Surface.Brick : Surface.Render;
        }
    }
}
