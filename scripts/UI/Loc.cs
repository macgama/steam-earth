using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace SteamEarth;

/// <summary>
/// Traductions de l'interface. Le texte français sert de clé (comme gettext) : les Label et Button dont le texte
/// correspond sont traduits automatiquement par Godot quand la langue change ; le texte construit en code passe par <see cref="T"/>.
/// Ajouter une langue : un code dans <see cref="Codes"/>, son nom dans <see cref="Names"/>, et une table comme <see cref="English"/>.
/// </summary>
public static class Loc
{
    public static readonly string[] Codes = { "fr", "en" };
    public static readonly string[] Names = { "Français", "English" };

    public static CultureInfo Culture { get; private set; } = new("fr-FR");
    static bool _registered;

    public static void SetLanguage(string code)
    {
        if (!_registered)
        {
            var en = new Translation { Locale = "en" };
            foreach (var (fr, tr) in English) en.AddMessage(fr, tr);
            TranslationServer.AddTranslation(en);
            _registered = true;
        }
        TranslationServer.SetLocale(code);
        Culture = code switch { "en" => new CultureInfo("en-GB"), _ => new CultureInfo("fr-FR") };
    }

    public static string T(string fr) => TranslationServer.Translate(fr);

    static readonly Dictionary<string, string> English = new()
    {
        // écran titre, pause
        ["La Terre entière, à l'échelle 1:1"] = "The whole Earth, at 1:1 scale",
        ["Jouer"] = "Play",
        ["Options"] = "Options",
        ["Commandes"] = "Controls",
        ["Crédits"] = "Credits",
        ["Quitter"] = "Quit",
        ["Quitter le jeu"] = "Quit game",
        ["Vraiment quitter ? (cliquer encore)"] = "Really quit? (click again)",
        ["Prototype"] = "Prototype",
        ["Pause"] = "Paused",
        ["Reprendre"] = "Resume",
        ["Changer de lieu"] = "Change location",
        ["Menu principal"] = "Main menu",
        ["Retour"] = "Back",

        // crédits
        ["Moteur"] = "Engine",
        ["Données géographiques"] = "Geographic data",
        ["Godot Engine (licence MIT) — godotengine.org"] = "Godot Engine (MIT licence) — godotengine.org",

        // options
        ["Graphismes"] = "Graphics",
        ["Qualité"] = "Quality",
        ["Réglages graphiques…"] = "Graphics settings…",
        ["Affichage"] = "Display",
        ["Plein écran (F11)"] = "Fullscreen (F11)",
        ["Taille de l'interface"] = "Interface size",
        ["Interface"] = "Interface",
        ["Langue"] = "Language",
        ["Unités"] = "Units",
        ["Métriques (km/h, m, °C)"] = "Metric (km/h, m, °C)",
        ["Impériales (mph, ft, °F)"] = "Imperial (mph, ft, °F)",
        ["Afficher le HUD (F2)"] = "Show HUD (F2)",
        ["Afficher la boussole"] = "Show compass",
        ["Informations techniques (F3)"] = "Technical information (F3)",
        ["Contrôles"] = "Controls",
        ["Sensibilité de la souris"] = "Mouse sensitivity",
        ["Inverser l'axe vertical de la souris"] = "Invert mouse vertical axis",
        ["Bas"] = "Low", ["Moyen"] = "Medium", ["Élevé"] = "High", ["Ultra"] = "Ultra",

        // HUD
        ["Altitude"] = "Altitude",
        ["{0} au-dessus du sol"] = "{0} above ground",
        ["heure décalée"] = "time shifted",
        ["vent"] = "wind",
        ["Météo : chargement…"] = "Weather: loading…",
        ["Vol libre"] = "Free flight",
        ["À pied"] = "On foot",
        ["En l'air"] = "Airborne",
        ["Transports publics"] = "Public transport",
        ["Voiture"] = "Car", ["Vélo"] = "Bike", ["Bateau"] = "Boat", ["Avion"] = "Plane",
        ["Ciel dégagé"] = "Clear sky", ["Peu nuageux"] = "Partly cloudy", ["Nuageux"] = "Cloudy", ["Couvert"] = "Overcast",
        ["Brouillard"] = "Fog", ["Pluie faible"] = "Light rain", ["Pluie"] = "Rain", ["Forte pluie"] = "Heavy rain",
        ["Neige"] = "Snow", ["Fortes chutes de neige"] = "Heavy snow",
        ["SO"] = "SW", ["O"] = "W", ["NO"] = "NW",
        ["Échap : menu"] = "Esc: menu", ["F1 : commandes"] = "F1: controls", ["F2 : masquer le HUD"] = "F2: hide HUD",
        ["© OpenStreetMap contributors · Crédits dans le menu"] = "© OpenStreetMap contributors · Credits in the menu",

        // commandes
        ["Se déplacer"] = "Moving",
        ["Avancer, aller à gauche, reculer, aller à droite"] = "Forward, left, back, right",
        ["Souris"] = "Mouse",
        ["Regarder autour de soi (clic pour reprendre la souris)"] = "Look around (click to capture the mouse)",
        ["{W} {A} {S} {D} ou ↑ ← ↓ →"] = "{W} {A} {S} {D} or ↑ ← ↓ →",
        ["{W} / {S} ou ↑ / ↓"] = "{W} / {S} or ↑ / ↓",
        ["Espace / {E}"] = "Space / {E}",
        ["Monter (vol libre)"] = "Up (free flight)",
        ["Descendre (vol libre)"] = "Down (free flight)",
        ["Maj"] = "Shift",
        ["Aller 8 fois plus vite"] = "Go 8 times faster",
        ["Molette"] = "Mouse wheel",
        ["Régler la vitesse de vol"] = "Adjust flight speed",
        ["Lieux célèbres : Interlaken, Zermatt, Léman, Europe, Himalaya, New York, Tokyo, Sydney, Rio"] = "Famous places: Interlaken, Zermatt, Lake Geneva, Europe, Himalaya, New York, Tokyo, Sydney, Rio",
        ["Marcher / revenir au vol libre"] = "Walk / back to free flight",
        ["Espace"] = "Space",
        ["Sauter"] = "Jump",
        ["Courir"] = "Run",
        ["Vue arrière / première personne"] = "Third person / first person view",
        ["Rapprocher ou éloigner la caméra"] = "Move the camera closer or further",
        ["Véhicules"] = "Vehicles",
        ["Choisir un véhicule : voiture, vélo, bateau, avion"] = "Choose a vehicle: car, bike, boat, plane",
        ["Monter dans le véhicule le plus proche / en descendre"] = "Get in the nearest vehicle / get out",
        ["Accélérer / freiner, puis reculer à l'arrêt (avion : piquer / cabrer)"] = "Accelerate / brake, then reverse when stopped (plane: pitch down / up)",
        ["Tourner (avion : roulis)"] = "Steer (plane: roll)",
        ["Maj / Ctrl"] = "Shift / Ctrl",
        ["Avion : plus / moins de gaz"] = "Plane: more / less throttle",
        ["Tourner (avion : palonnier)"] = "Steer (plane: rudder)",
        ["Avion : freins"] = "Plane: brakes",
        ["Monde"] = "World",
        ["Changer de lieu de départ (ville du monde ou clic sur la Terre)"] = "Change starting place (any city, or click on the Earth)",
        ["Aller dans un pays"] = "Go to a country",
        ["{K} / clic sur un nom"] = "{K} / click on a name",
        ["Construire les bâtiments d'une ville (Maj : démolir)"] = "Build a town's buildings (Shift: demolish)",
        ["Près d'un arrêt en Suisse : départs des transports publics"] = "Near a stop in Switzerland: public transport departures",
        ["Avancer / reculer l'heure (Maj : par quart d'heure)"] = "Move time forward / back (Shift: by 15 minutes)",
        ["Revenir à l'heure réelle"] = "Back to real time",
        ["Jour permanent"] = "Permanent daylight",
        ["Afficher / masquer frontières, routes, noms, transports"] = "Show / hide borders, roads, names, transport",
        ["Échap"] = "Esc",
        ["Menu pause"] = "Pause menu",
        ["Libérer la souris (pour cliquer sur un nom)"] = "Free the mouse (to click on a name)",
        ["Réglages graphiques"] = "Graphics settings",
        ["Cette aide"] = "This help",
        ["Masquer / afficher le HUD"] = "Hide / show the HUD",
        ["Informations techniques"] = "Technical information",
        ["Plein écran"] = "Fullscreen",

        // menus existants
        ["Où veux-tu commencer ?"] = "Where do you want to start?",
        ["Reculer (vue plus large)"] = "Zoom out (wider view)",
        ["Chercher une ville ou un pays…"] = "Search for a city or a country…",
        ["Fermer (M)"] = "Close (M)",
        ["Grandes villes :"] = "Major cities:",
        ["Ou clique sur la Terre pour t'en approcher, puis encore jusqu'à l'endroit exact."] = "Or click on the Earth to get closer, then again down to the exact spot.",
        ["Clique sur l'endroit où atterrir."] = "Click where you want to land.",
        ["Fermer (O)"] = "Close (O)",
        ["Réglage conseillé"] = "Recommended setting",
        ["Résolution 3D"] = "3D resolution",
        ["Synchronisation verticale (pas de déchirure de l'image)"] = "Vertical sync (no screen tearing)",
        ["Images par seconde max"] = "Max frames per second",
        ["Sans limite"] = "Unlimited",
        ["Rechercher un pays ou une capitale…"] = "Search for a country or a capital…",
        ["Chargement des villes et villages du monde…"] = "Loading the world's cities and villages…",
        ["Liste des villes indisponible : "] = "City list unavailable: ",
        ["Grandes villes ({0} villes et villages au total, tape un nom) :"] = "Major cities ({0} cities and villages in total, type a name):",
        ["Aucun résultat : clique plutôt sur la Terre."] = "No result: click on the Earth instead.",
        ["{0} — capitale {1}"] = "{0} — capital {1}",
        ["ville"] = "city",
        ["village"] = "village",
        ["localité"] = "place",
        ["hab."] = "inhab.",
    };
}
