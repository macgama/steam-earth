# Steam Earth

Prototype Godot 4.7 (C#) : **la Terre entière, ronde, à l'échelle 1:1**, avec son relief réel.
On peut aller de la marche à pied jusqu'à l'orbite sans changer de scène.

| Interlaken (lacs de Thoune et Brienz) | Vers le Cervin | Europe depuis 3 000 km |
|---|---|---|
| ![](docs/interlaken.jpg) | ![](docs/cervin.jpg) | ![](docs/europe-espace.jpg) |

## Lancer le projet

1. Installer **Godot 4.7.2 .NET** et le **SDK .NET 8**.
2. Cloner ce dépôt, puis dans Godot : *Importer* → choisir `project.godot`.
3. Appuyer sur **F5**. Au premier lancement, Godot compile le C#.

### Commandes

| Touche | Action |
|---|---|
| Clic gauche / Échap | capturer / libérer la souris |
| Z Q S D (W A S D en QWERTY) | se déplacer |
| Espace ou E / C ou A | monter / descendre |
| Maj | vitesse ×8 |
| Molette | multiplicateur de vitesse |
| F | mode marche avec un personnage : gravité vers le centre de la Terre, Espace = sauter, Maj = courir |
| V (en marche) | vue arrière (caméra derrière le personnage) / vue à la première personne |
| Souris / molette (en marche) | tourner la caméra autour du personnage / la rapprocher ou l'éloigner |
| 1 à 9 | Interlaken, Zermatt, Léman, Europe vue de l'espace, Himalaya, New York, Tokyo, Sydney, Rio |
| + / − (Maj : par ¼ h) | avancer / reculer l'heure (test) |
| R | revenir à l'heure réelle |
| Tab (ou bouton « Pays ») | menu des pays : clic = 500 m au-dessus de la capitale, recherche par nom |
| B | afficher / masquer frontières et capitales |
| T | afficher / masquer routes, chemins et voies ferrées |
| N | afficher / masquer les noms des villes, villages et rues |
| P | afficher / masquer les transports : gares, arrêts, débarcadères, aéroports |
| M | menu de départ : reprendre, chercher une ville du monde, ou cliquer sur la Terre |
| Clic sur un nom (souris libre) / K en visant un nom | « Berne — on construit ? » : construire les bâtiments de la localité (Maj : démolir) |

La vitesse s'adapte à la hauteur au-dessus du sol : lente près du sol, très rapide en orbite.

## Comment ça marche

- **Cube-sphère** (`Geo.cs`) : les 6 faces d'un cube projetées sur une sphère de 6 371 km.
- **Quadtree LOD** (`Planet.cs`) : chaque face est subdivisée près de la caméra (jusqu'au niveau 15, ≈ 10 m entre sommets ;
  jusqu'au niveau 19, ≈ 0,6 m, dans les zones haute définition comme la Suisse), et simplifiée au loin ; rien n'est détaillé derrière l'horizon. Les chunks sont construits en parallèle (`ChunkBuilder.cs`),
  avec des « jupes » pour masquer les fissures entre niveaux.
- **Précision** : positions en `double` (`Vec3d`). La caméra reste à l'origine de Godot et le monde est placé autour d'elle,
  tourné pour que la verticale locale soit l'axe Y (le ciel, le brouillard et la physique Godot restent corrects partout).
- **Altitudes** (`HeightProvider.cs`) : carte mondiale ~5 km incluse dans le jeu, puis **relief 30 m sur toute la Terre
  téléchargé à la demande** : `Data/CogReader.cs` lit directement les fichiers Copernicus et WorldCover hébergés sur
  AWS Open Data (requêtes HTTP partielles, seules les tuiles 1024×1024 utiles sont téléchargées) et les garde en cache
  dans `user://cache/`. Plus on est loin, plus on lit un aperçu grossier (60, 120, 240 m). Tant qu'une tuile n'est pas
  arrivée, le chunk utilise une donnée plus grossière puis se reconstruit tout seul.
  Les tuiles de `data/detail/` (Suisse) restent utilisées en priorité, sans réseau.
- **Suisse à 0,5 m** (`Data/SwissAlti3D.cs`) : près du sol (écart entre sommets < 16 m), le relief vient de
  **swissALTI3D** (swisstopo), le modèle du terrain nu à 0,5 m, en tuiles de 1 km en projection suisse LV95.
  Chaque fichier contient aussi des aperçus à 1, 2 et 4 m ; on ne télécharge que les blocs de 128 × 128 pixels utiles
  (≈ 50 Ko chacun, ~16 Mo pour un km² complet à 0,5 m, ~200 Ko à 4 m). Les coordonnées GPS sont converties en LV95
  (formules swisstopo) et l'URL de chaque tuile vient de `data/swissalti3d_index.bin` (année du relevé par km²).
  En attendant les données, c'est le 30 m qui s'affiche. Le même relief sert au contact des pieds en mode marche.

| Lauterbrunnen, avant (30 m) | Même endroit, swissALTI3D 0,5 m | Lauterbrunnen vu de 170 m | À pied près de Grindelwald |
|---|---|---|---|
| ![](docs/lauterbrunnen-30m.jpg) | ![](docs/lauterbrunnen-05m-sol.jpg) | ![](docs/lauterbrunnen-05m.jpg) | ![](docs/grindelwald-marche-05m.jpg) |

## Couverture des sols : forêts, champs, glaciers, villes

Le sol est coloré selon **ESA WorldCover 2021** (10 m, monde entier, CC BY 4.0) : forêts, broussailles, prairies,
champs, zones bâties, roche/désert, glaciers et neiges éternelles, zones humides, mangroves, mousses et lichens.

- Près du joueur (`LandCover.cs`) : la classe vient des mêmes fichiers WorldCover que l'eau, déjà téléchargés à la
  demande (aucun téléchargement en plus). Plus on est loin, plus on lit un aperçu grossier (10 m → 640 m).
- Vues lointaines (> 2 km entre sommets) : carte mondiale incluse dans le jeu, `data/landcover/world_cover.bin.gz`
  (0,05° ≈ 5 km, 0,9 Mo), générée par `tools/landcover/build_landcover_base.py` (classe majoritaire par cellule).
- `ChunkBuilder.cs` : une couleur par classe ; feuillus / conifères selon latitude et altitude ; champs en
  parcelles de couleurs différentes (blé, colza, labour, maïs…) ; pentes raides en roche (sauf forêts).
- `Vegetation.cs` : **arbres et buissons en 3D** dans un rayon d'environ 1,5 km (chunks de 10 et 20 m), placés là
  où WorldCover indique forêt, mangrove ou broussailles, plus quelques arbres isolés dans les prés.
  Positions fixes (on retrouve la même forêt), rendu par MultiMesh. Le compteur « arbres » du HUD donne leur nombre.

| Forêt près d'Interlaken | Forêts et champs près de Berne | Glacier d'Aletsch |
|---|---|---|
| ![](docs/sols-foret.jpg) | ![](docs/sols-forets-champs.jpg) | ![](docs/sols-glacier.jpg) |

Limites actuelles : pas de collision avec les arbres ; les routes apparaissent comme zones bâties (grises) dans
WorldCover ; la neige saisonnière n'est pas encore simulée (seuls glaciers et neiges éternelles sont blancs).

## Météo et heure réelles

Le jeu est synchronisé avec la réalité : **heure UTC réelle** (position du Soleil calculée, jour/nuit) et
**météo réelle** issue du modèle **NOAA GFS** (domaine public, usage commercial autorisé).

- `tools/weather/build_weather.py` télécharge sur AWS Open Data la dernière prévision GFS 1°
  (vent à 10 m et ~1 500 m, rafales, température, nuages bas/moyens/hauts, pluie, part de neige, visibilité)
  pour les 27 prochaines heures, et l'écrit dans un fichier compact (~3 Mo).
- Le workflow GitHub Actions `.github/workflows/weather.yml` le régénère **toutes les 6 h** et le publie dans la
  release `weather-latest` : c'est le « serveur météo », gratuit.
- Le jeu (`Weather.cs`) télécharge ce fichier au démarrage puis toutes les 30 min, le garde en cache, et interpole
  dans l'espace et le temps. Sans connexion, il utilise le cache ou `data/weather/fallback.bin.gz`.
- `Atmosphere.cs` en fait : soleil et ciel jour/nuit/couvert, brouillard selon la visibilité, trois couches de nuages
  poussées par le vent réel, couverture nuageuse mondiale vue de l'espace, pluie et neige.
- Pour les véhicules : `WeatherSample.WindAt(hauteur)` donne le vent (est, nord) en m/s.

### Fuseaux horaires

Le jour et la nuit dépendent de la vraie position du Soleil, donc de l'heure UTC et du lieu. L'**heure légale locale**
(fuseau, heure d'été) est donnée par `TimeZones.cs` :

- `data/timezones/timezones.bin.gz` (~750 Ko) : carte mondiale des 444 fuseaux IANA à 0,025° (~2,8 km), océans compris,
  générée par `tools/timezones/build_timezones.py` à partir de
  [timezone-boundary-builder](https://github.com/evansiroky/timezone-boundary-builder) (version 2026b).
- Les règles (décalage, dates d'heure d'été) viennent de la base IANA du système via .NET `TimeZoneInfo`,
  donc restent à jour avec Windows/macOS/Linux.
- `TimeZones.LocalTime(utc, lat, lon, …)` est utilisable partout dans le jeu (horaires, PNJ, éclairage public…).

Pour mettre à jour la carte quand une nouvelle version sort :
`python build_timezones.py combined-with-oceans.json ../../data/timezones/timezones.bin.gz 0.025`.

| Ciel réel sur Interlaken | Nuages GFS sur l'Europe | Terminateur jour/nuit |
|---|---|---|
| ![](docs/meteo-interlaken.jpg) | ![](docs/meteo-europe.jpg) | ![](docs/meteo-crepuscule.jpg) |

Après la fusion dans `main`, lancer une première fois le workflow à la main :
*Actions → Météo GFS → Run workflow*.

## Frontières et capitales

- `tools/geo/build_geo.py` extrait de **Natural Earth** (domaine public) les frontières terrestres et les capitales de
  203 pays et territoires (noms en français) → `data/geo/borders.bin.gz` et `data/geo/countries.json`.
- `PoliticalLayer.cs` : frontières en lignes fines posées sur le relief, capitales en étiquettes visibles à moins de ~800 km.
- `CountryMenu.cs` : liste alphabétique « Pays (Capitale) » avec recherche ; un clic place le joueur 500 m au-dessus de
  la capitale, 1,5 km au sud, en la regardant.
- Les frontières suivent la représentation *de facto* de Natural Earth ; quelques choix de capitales sont fixés dans le
  script (Pretoria, Astana, Dodoma, Porto-Novo…).

| Menu des pays | Arrivée au-dessus de Berne | Frontière franco-suisse sur le Léman |
|---|---|---|
| ![](docs/menu-pays.jpg) | ![](docs/capitale-berne.jpg) | ![](docs/frontiere-leman.jpg) |

## Routes, chemins, voies ferrées, villes et villages

Toutes les routes, tous les chemins et toutes les voies ferrées du monde, ainsi que les noms de toutes les villes,
villages et hameaux, viennent d'**OpenStreetMap**, via les tuiles vectorielles mondiales publiées chaque mois par
**Overture Maps** sur AWS Open Data (archives PMTiles : `transportation` 134 Go, `divisions` 20 Go). Comme pour le relief,
rien n'est dans le dépôt : le jeu ne télécharge (requêtes HTTP partielles) que les tuiles autour du joueur et les garde
dans `user://cache/overture/`.

- `Data/PmTiles.cs` : lecteur PMTiles v3 (répertoires, courbe de Hilbert, cache disque) ; `Data/Mvt.cs` : décodeur de
  tuiles vectorielles (protobuf) ; `Data/Overture.cs` : trouve la version la plus récente (les anciennes sont supprimées
  du serveur) et garde la liste en cache pour jouer hors ligne.
- `RoadLayer.cs` + `RoadBuilder.cs` : jusqu'à ~3 km (5 km en altitude), tuiles de zoom 14 avec **tous les tronçons** en
  rubans de leur vraie largeur (largeur OSM si connue), posés sur le relief 30 m : autoroutes, routes, rues, chemins
  agricoles, sentiers, escaliers, voies ferrées (ballast + deux rails), trams, funiculaires. Revêtements dessinés par un shader, sans texture à charger : asphalte avec lignes blanches (bords et ligne médiane
  en tirets) sur les grandes routes, pavés, chemins de gravier avec bande d'herbe, sentiers en terre ; voies ferrées
  en ballast clair avec traverses tous les 0,6 m et rails en acier brillant, trams noyés dans la chaussée.
  Les **ponts** sont tendus
  entre leurs culées, les **tunnels** et voies souterraines ne sont pas dessinés. Au-delà, jusqu'à ~20–60 km : tuiles
  de zoom 12 en lignes fines, masquées là où le détail est chargé.
- `PlacesLayer.cs` : noms des localités (villes, villes moyennes, villages, hameaux), en français quand OSM le connaît
  (Genève, Lucerne, Munich…). Plus le lieu est petit, plus il faut s'approcher : hameaux à 4 km, villages à 12 km,
  villes moyennes à 35 km, grandes villes jusqu'à 1 000 km. Les capitales gardent leur étiquette ◆.
- La mention **« © contributeurs OpenStreetMap (ODbL), Overture Maps Foundation »** est affichée en bas à droite de
  l'écran : elle est obligatoire (licence ODbL) et devra aussi figurer dans les crédits du jeu.

| Interlaken depuis 350 m | Sentiers du Harder | À pied sur un chemin |
|---|---|---|
| ![](docs/routes-interlaken.jpg) | ![](docs/chemins-harder.jpg) | ![](docs/marche-route.jpg) |

Pour la version commerciale, il sera plus sûr de copier les deux archives sur un serveur à nous (ou un CDN) plutôt que
de dépendre du bucket d'Overture : il suffit de changer `Overture.Bucket`.

## Transports : gares, arrêts, ports, aéroports, noms de rues

- `TransitLayer.cs` (touche P) : gares et haltes, stations de métro, arrêts de tram et de bus, gares routières,
  débarcadères et ports, funiculaires, téléphériques. Source : OpenStreetMap via Overture Maps, archive `base`
  (couche `infrastructure`, uniquement au zoom 13, ~3 km par tuile), chargée dans un rayon de ~3 km quand on est à moins
  de 2,5 km du sol. Le mode (train, métro, tram, bus…) est déduit de la classe Overture et des étiquettes OSM d'origine
  (`source_tags`). Les quais et poteaux d'un même arrêt (« Bern Bahnhof (Z) », « (H) »…) sont regroupés en un seul
  pictogramme. Les gares se voient à ~4,5 km, les arrêts de bus à ~550 m.
- Aéroports : `data/transport/airports.tsv.gz` (49 000 aéroports, aérodromes et hydrobases d'**OurAirports**, domaine
  public, `tools/transport/build_airports.py`), avec leur code IATA : grands aéroports visibles jusqu'à 350 km (d'avion),
  aéroports régionaux à 120 km, aérodromes à 15 km.
- `StreetNamesLayer.cs` (touche N) : noms des rues et routes (mêmes tuiles z14 que les routes, déjà en cache), au-dessus
  de la chaussée quand on s'en approche (~250 m pour une rue, 600 m pour une route principale).
- `LabelTileLayer.cs` : base commune (chargement par tuiles, fusion des doublons, fondu, altitude affinée en s'approchant).
- Pictogrammes : `data/transport/icons.rgba.gz`, dessinés depuis la police Noto Color Emoji (SIL OFL 1.1, voir
  `data/transport/LICENCES.md`) par `tools/transport/build_icons.py`.

| Berne : gare, arrêts de bus et de tram | Genève : débarcadères, gare routière, rues | Aéroport de Zurich d'avion |
|---|---|---|
| ![](docs/transports-berne.jpg) | ![](docs/transports-geneve.jpg) | ![](docs/aeroport-zurich.jpg) |

Les lignes et horaires (trains, bus, avions qui circulent) ne sont pas encore là : voir « Limites connues ».
## Menu de départ : commencer n'importe où

Au lancement (et ensuite avec **M**), le joueur choisit où commencer :

- **Reprendre** là où il était (position gardée toutes les 5 s dans `user://last_position.json`) ;
- **chercher une ville** parmi les 7 342 villes de Natural Earth (noms en français, `data/geo/cities.tsv.gz`, généré par
  `tools/geo/build_cities.py`) ou un pays : on arrive 500 m au-dessus et la ville est construite ;
- **cliquer sur la Terre** : chaque clic rapproche (de l'orbite jusqu'à ~25 km), le dernier clic atterrit à cet endroit
  exact, n'importe où sur la planète. Clic droit ou « Reculer » : vue plus large.

Les tests automatiques (`--at`, `--view`, `--country`, `--build`…) sautent ce menu ; `--start` le force.

## Bâtiments des villes et villages, à la demande

Les bâtiments réels ne sont construits que là où le joueur le demande :

- en survolant le nom d'une ville ou d'un village (souris libre après Échap, ou viseur au centre de l'écran en vol),
  l'invite **« Berne — on construit ? »** apparaît : **clic** (ou **K** en vol) pour construire, **Maj + clic / Maj + K**
  pour démolir. Pendant la construction, l'invite montre l'avancement (tuiles, nombre de bâtiments) ;
- choisir un pays dans le menu **Tab** construit aussi sa capitale.

La zone construite est un disque autour du centre : ~350 m pour un hameau, 700 m pour un village, 1,5 km pour une
petite ville, 4 km pour une capitale, 2 à 7 km pour une ville selon sa population, 8 km pour une mégapole.
Les **grandes villes s'agrandissent au fur et à mesure** : quand le joueur arrive à moins de 1,5 km du bord de la zone
déjà construite, elle grandit (jusqu'à 15 km pour une capitale, 6 à 20 km pour une ville, 30 km pour une mégapole) ;
seules les tuiles du nouvel anneau sont reconstruites.
Les villes construites sont gardées dans `user://cities.json` et réapparaissent au lancement suivant (leurs tuiles ne
sont chargées que lorsqu'on est à moins de ~60 km ; visibles jusqu'à ~40 km).

- Source : **Overture Maps « buildings »** (`buildings.pmtiles`, même bucket AWS que les routes, 180 Go dont on ne lit
  que les tuiles de zoom 14 utiles, ~0,2 à 1,4 Mo chacune). Contours OpenStreetMap complétés par Microsoft ML Buildings
  (ODbL), Google Open Buildings (CC BY 4.0) et USGS Lidar (domaine public) : usage commercial autorisé avec mention.
- `BuildingBuilder.cs` : murs extrudés depuis le contour (cours intérieures comprises), posés sur le relief (le plus bas
  point du contour, fondations de 6 m sous le sol pour ne jamais flotter). Hauteur = `height` d'OSM, sinon nombre
  d'étages × 3 m, sinon une valeur selon le type (maison, immeuble, église, industrie…) et la surface.
  Toits : à deux pans, à quatre pans, en pavillon ou en appentis quand OSM le dit (ou par défaut pour les petites
  maisons à peu près rectangulaires), sinon plats. Les « building parts » d'OSM (églises, tours…) remplacent le contour.
  Couleurs de façade et de toit d'OSM quand elles existent, sinon selon le matériau ou le type.
- `BuildingsLayer.cs` : invite, liste des villes, tuiles, et un shader qui dessine les fenêtres (étages de 3 m) et les
  allume au hasard à la tombée de la nuit.

Exemple : Berne (rayon 4 km) = 20 825 bâtiments, ~30 tuiles.

| Berne, la vieille ville et la cathédrale | Lauterbrunnen | Berne agrandie, à la tombée de la nuit | Menu de départ |
|---|---|---|---|
| ![](docs/batiments-berne.jpg) | ![](docs/batiments-lauterbrunnen.jpg) | ![](docs/batiments-berne-nuit.jpg) | ![](docs/menu-depart.jpg) |

Pistes suivantes : en Suisse, **swissBUILDINGS3D 3.0** (swisstopo, vrais toits 3D, gratuit et commercial) à
convertir dans notre format ; collisions avec les murs en mode marche ; pas d'arbres à l'intérieur des bâtiments ;
textures de façades.

## Données (toutes gratuites)

| Fichier | Source | Résolution |
|---|---|---|
| `data/world_base.hgt.gz` | Tuiles Terrarium (AWS Open Data : SRTM, GMTED, ETOPO1…), avec bathymétrie | ~5 km |
| Relief mondial (streaming) | Copernicus DEM GLO-30, bucket AWS `copernicus-dem-30m` | 30 m |
| Eau et couverture des sols (streaming) | ESA WorldCover 2021, bucket AWS `esa-worldcover` (CC BY 4.0) | 10 m |
| `data/landcover/world_cover.bin.gz` | ESA WorldCover 2021 © ESA WorldCover project / Copernicus Sentinel data 2021, CC BY 4.0 | ~5 km |
| `data/detail/*.hgt.gz` | Copernicus DEM GLO-30 © DLR e.V. 2010-2014 et © Airbus 2014-2018, fourni par l'ESA | 30 m |
| `data/detail/*.wat.gz` | ESA WorldCover 2021 (classe « eau ») © ESA | 10 m → 30 m |
| Relief Suisse (streaming) | swissALTI3D © swisstopo (données ouvertes OGD, usage commercial autorisé avec mention de la source) | 0,5 m |
| `data/swissalti3d_index.bin` | index des tuiles swissALTI3D (API STAC de data.geo.admin.ch), `tools/swissalti3d/build_index.py` | 1 km |
| Météo | NOAA GFS (domaine public), via AWS Open Data | 1°, toutes les 3 h |
| `data/geo/*` | Natural Earth 1:10m (domaine public) | ~1 km |
| `data/timezones/timezones.bin.gz` | timezone-boundary-builder, © contributeurs OpenStreetMap, licence ODbL | 0,025° |
| Bâtiments (streaming, à la demande) | Overture Maps buildings : © contributeurs OpenStreetMap (ODbL), Microsoft ML Buildings (ODbL), Google Open Buildings (CC BY 4.0), USGS (domaine public) | tuiles z14 |
| Arrêts, gares, ports (streaming) | OpenStreetMap via Overture Maps, archive `base` (même licence ODbL) | tuiles z13 (~5 km) |
| `data/geo/cities.tsv.gz` | Natural Earth 1:10m populated places (domaine public) | 7 342 villes |
| `data/transport/airports.tsv.gz` | OurAirports (domaine public) | — |
| Routes, chemins, rails, localités (streaming) | OpenStreetMap via Overture Maps (bucket AWS `overturemaps-extras-us-west-2`), © contributeurs OpenStreetMap, licence ODbL | tuiles z14 (~2,4 km) |

Le relief 30 m couvre toute la Terre en streaming (connexion Internet nécessaire la première fois ; ensuite le cache suffit).
Les tuiles locales `data/detail/` (45–48°N, 5–11°E) servent de version hors ligne pour la Suisse.

### Ajouter une région

```bash
pip install rasterio numpy pillow
cd tools/pipeline
python build_detail_tiles.py ../../data/detail LAT_MIN LAT_MAX LON_MIN LON_MAX
# ex. la Corse : python build_detail_tiles.py ../../data/detail 41 43 8 10
```

Ce n'est plus nécessaire pour avoir du 30 m (le streaming couvre la Terre), seulement pour jouer hors ligne
sur une région. Chaque tuile pèse ~8 Mo.

Format des fichiers : en-tête 16 octets (`SEH1`, largeur, hauteur, 0) puis int16 codés en delta par ligne, le tout en gzip.

### Export du jeu

Les fichiers `.gz` ne sont pas des ressources Godot : dans *Projet → Exporter → Ressources*, ajouter
`data/*` dans « Filtres pour exporter des fichiers non-ressources ».

## Limites connues / prochaines étapes

- Couleurs par sommet provisoires → textures + shader (herbe, roche, neige selon pente et altitude).
- Relief plus fin que 30 m : pays par pays (Suisse 0,5 m avec swissALTI3D, France 1 m avec le RGE ALTI…).
- Copernicus n'a pas de fichier pour l'Arménie et l'Azerbaïdjan (relief à ~5 km là-bas pour l'instant).
- Transports qui circulent : tracés des lignes (relations OSM `route=bus/tram/train/subway/ferry`, ODbL) et horaires
  réels (GTFS : opentransportdata.swiss pour la Suisse, transport.data.gouv.fr pour la France…, licence à vérifier
  flux par flux pour un jeu vendu). Avions : OurAirports + lignes aériennes à reconstituer (pas de source gratuite
  d'horaires mondiaux utilisable commercialement ; OpenSky est réservé au non commercial).
- Océan = surface plate colorée → shader d'eau, vagues, physique de flottaison.
- Atmosphère : ciel procédural Godot → diffusion atmosphérique vue de l'espace.
- Collisions physiques pour les véhicules (colliders générés pour les chunks proches).
- Rivières plus fines (OpenStreetMap / HydroRIVERS).
- Routes : rubans posés sur le relief, sans aplanir le terrain ni collisions pour les véhicules ; noms de rues pas encore affichés.
