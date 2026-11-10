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
| N | afficher / masquer les noms des villes et villages |

La vitesse s'adapte à la hauteur au-dessus du sol : lente près du sol, très rapide en orbite.

## Comment ça marche

- **Cube-sphère** (`Geo.cs`) : les 6 faces d'un cube projetées sur une sphère de 6 371 km.
- **Quadtree LOD** (`Planet.cs`) : chaque face est subdivisée près de la caméra (jusqu'au niveau 15, ≈ 10 m entre sommets),
  et simplifiée au loin ; rien n'est détaillé derrière l'horizon. Les chunks sont construits en parallèle (`ChunkBuilder.cs`),
  avec des « jupes » pour masquer les fissures entre niveaux.
- **Précision** : positions en `double` (`Vec3d`). La caméra reste à l'origine de Godot et le monde est placé autour d'elle,
  tourné pour que la verticale locale soit l'axe Y (le ciel, le brouillard et la physique Godot restent corrects partout).
- **Altitudes** (`HeightProvider.cs`) : carte mondiale ~5 km incluse dans le jeu, puis **relief 30 m sur toute la Terre
  téléchargé à la demande** : `Data/CogReader.cs` lit directement les fichiers Copernicus et WorldCover hébergés sur
  AWS Open Data (requêtes HTTP partielles, seules les tuiles 1024×1024 utiles sont téléchargées) et les garde en cache
  dans `user://cache/`. Plus on est loin, plus on lit un aperçu grossier (60, 120, 240 m). Tant qu'une tuile n'est pas
  arrivée, le chunk utilise une donnée plus grossière puis se reconstruit tout seul.
  Les tuiles de `data/detail/` (Suisse) restent utilisées en priorité, sans réseau.

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
  agricoles, sentiers, escaliers, voies ferrées (ballast + deux rails), trams, funiculaires. Les **ponts** sont tendus
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

## Données (toutes gratuites)

| Fichier | Source | Résolution |
|---|---|---|
| `data/world_base.hgt.gz` | Tuiles Terrarium (AWS Open Data : SRTM, GMTED, ETOPO1…), avec bathymétrie | ~5 km |
| Relief mondial (streaming) | Copernicus DEM GLO-30, bucket AWS `copernicus-dem-30m` | 30 m |
| Eau et couverture des sols (streaming) | ESA WorldCover 2021, bucket AWS `esa-worldcover` (CC BY 4.0) | 10 m |
| `data/landcover/world_cover.bin.gz` | ESA WorldCover 2021 © ESA WorldCover project / Copernicus Sentinel data 2021, CC BY 4.0 | ~5 km |
| `data/detail/*.hgt.gz` | Copernicus DEM GLO-30 © DLR e.V. 2010-2014 et © Airbus 2014-2018, fourni par l'ESA | 30 m |
| `data/detail/*.wat.gz` | ESA WorldCover 2021 (classe « eau ») © ESA | 10 m → 30 m |
| Météo | NOAA GFS (domaine public), via AWS Open Data | 1°, toutes les 3 h |
| `data/geo/*` | Natural Earth 1:10m (domaine public) | ~1 km |
| `data/timezones/timezones.bin.gz` | timezone-boundary-builder, © contributeurs OpenStreetMap, licence ODbL | 0,025° |
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
- Océan = surface plate colorée → shader d'eau, vagues, physique de flottaison.
- Atmosphère : ciel procédural Godot → diffusion atmosphérique vue de l'espace.
- Collisions physiques pour les véhicules (colliders générés pour les chunks proches).
- Rivières plus fines (OpenStreetMap / HydroRIVERS).
- Routes : rubans posés sur le relief, sans aplanir le terrain ni collisions pour les véhicules ; noms de rues pas encore affichés.
