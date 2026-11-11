using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>Fonction réelle d'un bâtiment : elle décide de sa forme, de son aspect et de son intérieur.</summary>
public enum BuildingKind
{
    Generic, House, Apartments, Office, Shop, Supermarket, Restaurant, Hotel,
    Church, Chapel, Cathedral, Mosque, Temple,
    School, Kindergarten, University, Hospital, Civic, Library, Museum,
    Stadium, Grandstand, SportsHall, TrainStation, FireStation,
    Industrial, Warehouse, Farm, Garage, Shed, Greenhouse, Roof, Tank,
}

/// <summary>Motif des fenêtres dessiné par le shader des bâtiments (UV2.x).</summary>
public enum WindowStyle
{
    None = 0, Residential = 1, Office = 2, Church = 3, ShopResidential = 4, Industrial = 5, School = 6, Glass = 7,
    Door = 8, ShopOffice = 9, Seats = 10, Pitch = 11, Hall = 12, Awning = 13,
}

/// <summary>
/// Détermine la fonction d'un bâtiment à partir de ce que disent les données (par ordre de confiance) :
/// 1. la classe OSM du bâtiment (building=church, school, retail…) transmise par Overture ;
/// 2. les lieux Overture Places situés dans son contour (magasins, restaurants, écoles, hôpitaux…) ;
/// 3. son nom (« Kirche », « école », « Stadion »…) ;
/// 4. la zone OSM qui le contient (amenity=school, leisure=stadium, landuse=religious…) ;
/// 5. le sous-type Overture (residential, commercial, education…) puis la taille.
/// </summary>
public static class BuildingKinds
{
    /// <summary>Résumé des lieux (Overture Places) situés dans un bâtiment.</summary>
    public sealed class PlaceSummary
    {
        public int Shops, Food, Total;
        public BuildingKind? Strong;          // lieu qui donne à lui seul la fonction (église, école, hôpital…)
        public readonly List<(string name, string category)> Names = new();
    }

    public static BuildingKind FromClass(string cls) => cls switch
    {
        "house" or "detached" or "semidetached_house" or "semi" or "bungalow" or "cabin" or "terrace" or "static_caravan" or "stilt_house" or "trullo" or "farm" => cls == "farm" ? BuildingKind.Farm : BuildingKind.House,
        "apartments" or "residential" or "dormitory" => BuildingKind.Apartments,
        "office" or "government" or "bank" => BuildingKind.Office,
        "commercial" => BuildingKind.Office,
        "retail" or "kiosk" => BuildingKind.Shop,
        "supermarket" => BuildingKind.Supermarket,
        "hotel" => BuildingKind.Hotel,
        "church" or "religious" or "presbytery" or "monastery" => BuildingKind.Church,
        "chapel" or "shrine" or "wayside_shrine" => BuildingKind.Chapel,
        "cathedral" => BuildingKind.Cathedral,
        "mosque" => BuildingKind.Mosque,
        "temple" or "synagogue" => BuildingKind.Temple,
        "school" => BuildingKind.School,
        "kindergarten" => BuildingKind.Kindergarten,
        "university" or "college" => BuildingKind.University,
        "hospital" => BuildingKind.Hospital,
        "civic" or "public" or "post_office" or "toilets" => BuildingKind.Civic,
        "library" => BuildingKind.Library,
        "museum" => BuildingKind.Museum,
        "stadium" => BuildingKind.Stadium,
        "grandstand" or "pavilion" => BuildingKind.Grandstand,
        "sports_hall" or "sports_centre" or "riding_hall" => BuildingKind.SportsHall,
        "train_station" or "transportation" or "hangar" => BuildingKind.TrainStation,
        "fire_station" => BuildingKind.FireStation,
        "industrial" or "manufacture" or "service" or "military" or "bunker" => BuildingKind.Industrial,
        "warehouse" or "storage" => BuildingKind.Warehouse,
        "barn" or "farm_auxiliary" or "stable" or "cowshed" or "sty" or "agricultural" or "slurry_tank" => BuildingKind.Farm,
        "garage" or "garages" or "carport" or "parking" => BuildingKind.Garage,
        "shed" or "hut" or "outbuilding" or "boathouse" or "beach_hut" or "guardhouse" or "allotment_house" => BuildingKind.Shed,
        "greenhouse" or "glasshouse" => BuildingKind.Greenhouse,
        "roof" or "shelter" => BuildingKind.Roof,
        "silo" or "storage_tank" or "transformer_tower" => BuildingKind.Tank,
        _ => BuildingKind.Generic,
    };

    static BuildingKind FromSubtype(string sub, double area) => sub switch
    {
        "residential" => area < 160 ? BuildingKind.House : BuildingKind.Apartments,
        "commercial" => BuildingKind.Office,
        "education" => BuildingKind.School,
        "religious" => BuildingKind.Church,
        "medical" => BuildingKind.Hospital,
        "civic" => BuildingKind.Civic,
        "entertainment" => BuildingKind.SportsHall,
        "transportation" => BuildingKind.TrainStation,
        "industrial" => BuildingKind.Industrial,
        "agricultural" => BuildingKind.Farm,
        "outbuilding" => BuildingKind.Shed,
        _ => BuildingKind.Generic,
    };

    /// <summary>Lieu Overture Places → fonction forte (null : simple commerce ou bureau).</summary>
    public static BuildingKind? FromPlace(string category, string[] hierarchy, out bool shop, out bool food)
    {
        shop = food = false;
        string c = category ?? "";
        string top = hierarchy != null && hierarchy.Length > 0 ? hierarchy[0] : "";
        if (Has(c, "church", "cathedral", "chapel", "christian", "catholic", "protestant", "orthodox", "evangelical", "baptist", "anglican", "lutheran", "methodist", "presbyterian"))
            return c.Contains("cathedral") ? BuildingKind.Cathedral : BuildingKind.Church;
        if (Has(c, "mosque")) return BuildingKind.Mosque;
        if (Has(c, "synagogue", "temple", "buddhist", "hindu")) return BuildingKind.Temple;
        if (Has(c, "stadium", "arena")) return BuildingKind.Stadium;
        if (Has(c, "kindergarten", "preschool", "day_care", "child_care")) return BuildingKind.Kindergarten;
        if (Has(c, "college_university", "university", "college")) return BuildingKind.University;
        if (Has(c, "elementary_school", "high_school", "middle_school", "primary_school", "secondary_school", "public_school", "private_school")) return BuildingKind.School;
        if (c == "hospital" || Has(c, "general_hospital", "children_hospital")) return BuildingKind.Hospital;
        if (Has(c, "train_station", "railway_station", "bus_station")) return BuildingKind.TrainStation;
        if (Has(c, "fire_station", "fire_department")) return BuildingKind.FireStation;
        if (Has(c, "library")) return BuildingKind.Library;
        if (Has(c, "museum", "art_gallery")) return BuildingKind.Museum;
        if (Has(c, "supermarket", "grocery_store", "hypermarket", "department_store", "shopping_center", "shopping_mall")) { shop = true; return BuildingKind.Supermarket; }
        if (Has(c, "hotel", "motel", "hostel")) return BuildingKind.Hotel;
        if (Has(c, "sports_hall", "gym", "fitness", "sport_or_fitness", "swimming_pool", "ice_rink")) return null;
        if (top == "shopping" || Has(c, "_store", "shop", "bakery", "butcher", "pharmacy", "florist", "boutique", "market")) { shop = true; return null; }
        if (top == "food_and_drink" || Has(c, "restaurant", "cafe", "coffee", "bar", "pub", "eatery", "pizza", "bistro")) { food = true; return null; }
        return null;
    }

    static bool Has(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Nom du bâtiment (plusieurs langues) → fonction, quand les étiquettes ne disent rien.</summary>
    public static BuildingKind? FromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string n = name.ToLowerInvariant();
        if (Has(n, "cathédrale", "cathedral", "münster", "kathedrale", "duomo", "catedral", "dom ")) return BuildingKind.Cathedral;
        if (Has(n, "kirche", "église", "eglise", "church", "chiesa", "iglesia", "temple protestant", "kerk", "kościół", "basilique", "basilica")) return BuildingKind.Church;
        if (Has(n, "kapelle", "chapelle", "chapel", "cappella", "capilla")) return BuildingKind.Chapel;
        if (Has(n, "moschee", "mosquée", "mosque", "moschea", "mezquita", "camii")) return BuildingKind.Mosque;
        if (Has(n, "synagog")) return BuildingKind.Temple;
        if (Has(n, "stadion", "stade ", "stadium", "stadio", "estadio", "arena")) return BuildingKind.Stadium;
        if (Has(n, "kindergarten", "crèche", "creche", "jardin d'enfants", "kita")) return BuildingKind.Kindergarten;
        if (Has(n, "universit", "hochschule", "eth ", "epfl", "college", "collège")) return BuildingKind.University;
        if (Has(n, "schul", "école", "ecole", "school", "scuola", "escuela", "gymnas", "lycée", "lycee")) return BuildingKind.School;
        if (Has(n, "spital", "hôpital", "hopital", "hospital", "klinik", "clinique", "ospedale")) return BuildingKind.Hospital;
        if (Has(n, "bahnhof", "gare ", "gare de", "station", "stazione", "estación")) return BuildingKind.TrainStation;
        if (Has(n, "hotel", "hôtel")) return BuildingKind.Hotel;
        if (Has(n, "bibliothe", "library", "biblioteca")) return BuildingKind.Library;
        if (Has(n, "museum", "musée", "musee", "museo")) return BuildingKind.Museum;
        if (Has(n, "feuerwehr", "pompiers", "fire station")) return BuildingKind.FireStation;
        if (Has(n, "migros", "coop", "aldi", "lidl", "denner", "carrefour", "manor", "supermar", "einkaufszentrum", "centre commercial")) return BuildingKind.Supermarket;
        if (Has(n, "turnhalle", "sporthalle", "salle de sport", "gymnase", "halle de sport")) return BuildingKind.SportsHall;
        return null;
    }

    /// <summary>Zone OSM (Overture base / land_use) qui contient le bâtiment → fonction.</summary>
    public static BuildingKind? FromLandUse(string cls) => cls switch
    {
        "school" or "education" => BuildingKind.School,
        "kindergarten" => BuildingKind.Kindergarten,
        "university" or "college" => BuildingKind.University,
        "hospital" or "clinic" => BuildingKind.Hospital,
        "stadium" => BuildingKind.Stadium,
        "religious" => BuildingKind.Church,
        "railway" or "station" => BuildingKind.TrainStation,
        "industrial" or "works" => BuildingKind.Industrial,
        _ => null,
    };

    public static BuildingKind Classify(string cls, string sub, string name, PlaceSummary places, string landUse, double area, double height)
    {
        var k = FromClass(cls);
        // « bâtiment générique » ou résidentiel avec des commerces au rez-de-chaussée
        if (places?.Strong is BuildingKind strong && (k is BuildingKind.Generic or BuildingKind.Office or BuildingKind.Civic || (strong is BuildingKind.Stadium && area > 3000)))
            k = strong;
        if (k == BuildingKind.Generic && FromName(name) is BuildingKind byName) k = byName;
        if (k == BuildingKind.Generic && places != null && (places.Shops > 0 || places.Food > 0))
            k = places.Shops > 0 && area > 1500 && height < 14 ? BuildingKind.Supermarket : places.Food > places.Shops ? BuildingKind.Restaurant : BuildingKind.Shop;
        if (k == BuildingKind.Generic && landUse != null && FromLandUse(landUse) is BuildingKind byZone)
        {
            // dans l'enceinte d'une école, d'un hôpital… : seuls les vrais bâtiments en prennent la fonction (pas les cabanons)
            if (area >= (byZone == BuildingKind.Stadium ? 2000 : 120)) k = byZone;
        }
        if (k == BuildingKind.Generic) k = FromSubtype(sub, area);
        if (k == BuildingKind.Generic) k = area < 25 ? BuildingKind.Shed : area < 180 && height < 11 ? BuildingKind.House : height >= 9 ? BuildingKind.Apartments : BuildingKind.House;
        return k;
    }

    /// <summary>Les commerces au rez-de-chaussée (vitrines, enseignes) d'un immeuble d'habitation ou de bureaux.</summary>
    public static bool HasShopfront(BuildingKind k, PlaceSummary p) =>
        k is BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Restaurant
        || (p != null && (p.Shops > 0 || p.Food > 0) && k is BuildingKind.Apartments or BuildingKind.Office or BuildingKind.House or BuildingKind.Hotel or BuildingKind.Civic);

    public static WindowStyle Windows(BuildingKind k, bool shopfront, double height, double area) => k switch
    {
        _ when shopfront => k is BuildingKind.Office or BuildingKind.Civic ? WindowStyle.ShopOffice
            : k is BuildingKind.Supermarket && height < 12 ? WindowStyle.Glass : WindowStyle.ShopResidential,
        BuildingKind.House or BuildingKind.Apartments or BuildingKind.Hotel or BuildingKind.Generic => WindowStyle.Residential,
        BuildingKind.Office or BuildingKind.Civic or BuildingKind.Hospital or BuildingKind.Library or BuildingKind.Museum =>
            height > 30 || (k == BuildingKind.Office && area > 1500 && height > 18) ? WindowStyle.Glass : WindowStyle.Office,
        BuildingKind.Church or BuildingKind.Chapel or BuildingKind.Cathedral or BuildingKind.Mosque or BuildingKind.Temple => WindowStyle.Church,
        BuildingKind.School or BuildingKind.Kindergarten or BuildingKind.University => WindowStyle.School,
        BuildingKind.SportsHall or BuildingKind.TrainStation => WindowStyle.Hall,
        BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.FireStation => WindowStyle.Industrial,
        BuildingKind.Greenhouse => WindowStyle.Glass,
        BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Restaurant => WindowStyle.ShopResidential,
        BuildingKind.Stadium or BuildingKind.Grandstand => WindowStyle.Office,
        BuildingKind.Farm => height > 5 ? WindowStyle.Residential : WindowStyle.None,
        _ => WindowStyle.None,
    };

    /// <summary>Couleur de façade quand OSM n'en donne pas.</summary>
    public static Color Wall(BuildingKind k, double seed)
    {
        int pick = (int)(seed * 4) & 3;
        return k switch
        {
            BuildingKind.Church or BuildingKind.Chapel or BuildingKind.Cathedral => pick < 2 ? new Color(0.70f, 0.66f, 0.56f) : new Color(0.62f, 0.60f, 0.55f),
            BuildingKind.Mosque or BuildingKind.Temple => new Color(0.90f, 0.87f, 0.80f),
            BuildingKind.School or BuildingKind.University => pick switch { 0 => new Color(0.86f, 0.74f, 0.52f), 1 => new Color(0.78f, 0.55f, 0.42f), 2 => new Color(0.82f, 0.80f, 0.74f), _ => new Color(0.70f, 0.42f, 0.32f) },
            BuildingKind.Kindergarten => pick switch { 0 => new Color(0.95f, 0.78f, 0.35f), 1 => new Color(0.55f, 0.75f, 0.55f), 2 => new Color(0.90f, 0.55f, 0.45f), _ => new Color(0.55f, 0.70f, 0.85f) },
            BuildingKind.Hospital => new Color(0.92f, 0.92f, 0.90f),
            BuildingKind.Office or BuildingKind.Civic or BuildingKind.Library or BuildingKind.Museum => pick < 2 ? new Color(0.80f, 0.79f, 0.76f) : new Color(0.70f, 0.70f, 0.68f),
            BuildingKind.Shop or BuildingKind.Restaurant => new Color(0.88f, 0.82f, 0.72f),
            BuildingKind.Supermarket => new Color(0.82f, 0.82f, 0.80f),
            BuildingKind.Stadium or BuildingKind.Grandstand => new Color(0.74f, 0.75f, 0.76f),
            BuildingKind.SportsHall => new Color(0.72f, 0.76f, 0.80f),
            BuildingKind.TrainStation => new Color(0.78f, 0.72f, 0.62f),
            BuildingKind.FireStation => new Color(0.72f, 0.26f, 0.22f),
            BuildingKind.Industrial or BuildingKind.Warehouse => new Color(0.62f, 0.64f, 0.66f),
            BuildingKind.Farm or BuildingKind.Shed => new Color(0.52f, 0.38f, 0.25f),
            BuildingKind.Greenhouse => new Color(0.70f, 0.82f, 0.84f),
            BuildingKind.Garage or BuildingKind.Tank => new Color(0.70f, 0.70f, 0.68f),
            _ => new Color(0.86f, 0.82f, 0.74f), // crépi clair
        };
    }

    /// <summary>Bâtiments où l'on peut entrer (porte d'entrée et intérieur).</summary>
    public static bool Enterable(BuildingKind k) => k is not (BuildingKind.Garage or BuildingKind.Shed or BuildingKind.Roof
        or BuildingKind.Tank or BuildingKind.Stadium or BuildingKind.Grandstand or BuildingKind.Greenhouse);

    /// <summary>Grand volume d'un seul tenant (nef, halle, gymnase) sauf si OSM donne des étages.</summary>
    public static bool SingleHall(BuildingKind k) => k is BuildingKind.Church or BuildingKind.Chapel or BuildingKind.Cathedral
        or BuildingKind.Mosque or BuildingKind.Temple or BuildingKind.SportsHall or BuildingKind.TrainStation
        or BuildingKind.Warehouse or BuildingKind.Industrial or BuildingKind.Farm or BuildingKind.Supermarket;

    public static string Label(BuildingKind k) => k switch
    {
        BuildingKind.House => "maison", BuildingKind.Apartments => "immeuble", BuildingKind.Office => "bureaux",
        BuildingKind.Shop => "magasin", BuildingKind.Supermarket => "supermarché", BuildingKind.Restaurant => "restaurant",
        BuildingKind.Hotel => "hôtel", BuildingKind.Church => "église", BuildingKind.Chapel => "chapelle",
        BuildingKind.Cathedral => "cathédrale", BuildingKind.Mosque => "mosquée", BuildingKind.Temple => "temple",
        BuildingKind.School => "école", BuildingKind.Kindergarten => "jardin d'enfants", BuildingKind.University => "université",
        BuildingKind.Hospital => "hôpital", BuildingKind.Civic => "bâtiment public", BuildingKind.Library => "bibliothèque",
        BuildingKind.Museum => "musée", BuildingKind.Stadium => "stade", BuildingKind.Grandstand => "tribune",
        BuildingKind.SportsHall => "salle de sport", BuildingKind.TrainStation => "gare", BuildingKind.FireStation => "caserne de pompiers",
        BuildingKind.Industrial => "bâtiment industriel", BuildingKind.Warehouse => "entrepôt", BuildingKind.Farm => "ferme",
        BuildingKind.Garage => "garage", BuildingKind.Shed => "cabane", BuildingKind.Greenhouse => "serre", BuildingKind.Roof => "abri",
        BuildingKind.Tank => "réservoir", _ => "bâtiment",
    };
}
