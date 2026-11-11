# Qualité graphique : Bas / Moyen / Élevé / Ultra

**Règle du projet** : toute amélioration graphique doit avoir plusieurs niveaux, pour que les machines puissantes
profitent de tous les détails et que les machines avec une carte graphique basique puissent quand même jouer.

Tout passe par un seul point d'entrée : `GraphicsQuality` (`scripts/Graphics/GraphicsQuality.cs`).

## Pour le joueur

- **Premier lancement** : le jeu lit le nom de la carte graphique, la mémoire et le nombre de cœurs, choisit un niveau
  et l'annonce en haut de l'écran (« Graphismes réglés sur Moyen pour ta machine… »).
- **Touche O** (ou F10) : menu Graphismes. Niveau Bas / Moyen / Élevé / Ultra (★ = conseillé), résolution 3D
  (50–100 %, mise à l'échelle FSR), synchronisation verticale, limite d'images par seconde, bouton « Réglage conseillé ».
- Les changements s'appliquent tout de suite, sans relancer, et sont gardés dans `user://settings.cfg`, section `[graphismes]`.

## Ce que règle chaque niveau

| | Bas | Moyen | Élevé | Ultra |
|---|---|---|---|---|
| Finesse du relief (`TerrainSplitFactor`) | 1,15 | 1,35 | 1,6 | 2,0 |
| Relief 0,5 m Suisse (`TerrainHdMaxLevel`) | 2,4 m | 1,2 m | 0,6 m | 0,6 m |
| Arbres : distance (`VegetationMaxSpacing`) | ~700 m | ~1,5 km | ~1,5 km | ~3 km |
| Arbres : densité (`VegetationDensity`) | 40 % | 70 % | 100 % | 100 % |
| Herbe et cailloux (`GrassDensity` / `GrassDistance`) | non | 35 % / 40 m | 70 % / 80 m | 100 % / 150 m |
| Textures du sol (`GroundDetail`, 0–3) | 1 | 2 | 3 | 3 |
| Bâtiments affichés jusqu'à | 10 km | 20 km | 40 km | 60 km |
| Intérieurs des bâtiments (`InteriorRadius` / `InteriorMax`) | 25 m / 6 | 45 m / 12 | 80 m / 24 | 140 m / 40 |
| Mobilier (`FurnitureDetail`, 0–3) / étages meublés (`InteriorFloorSpan`) | 0 / le sien | 1 / ± 1 | 2 / ± 1 | 3 / ± 2 |
| Enseignes des commerces (`SignDistance`) | 40 m | 80 m | 140 m | 220 m |
| Routes, noms (rayon ×) | 0,6 / 0,7 | 0,8 / 0,85 | 1 / 1 | 1,3 / 1,2 |
| Ombres du soleil | non | 300 m, bâtiments | 1 km, + relief et arbres | 3 km, douces, + petits objets |
| Anticrénelage | FXAA | MSAA 2× | MSAA 4× | MSAA 8× |
| Occlusion ambiante (SSAO) / halo | non / non | non / oui | oui / oui | oui / oui |
| Résolution 3D par défaut | 80 % | 100 % | 100 % | 100 % |
| Nuages (subdivisions) / pluie et neige | 16 / 30 % | 32 / 60 % | 48 / 100 % | 96 / 150 % |
| Distance générale (`DrawDistanceScale`) | 0,5 | 0,75 | 1 | 1,4 |
| Eau : vagues additionnées (`WaterWaves`) | 16 | 32 | 48 | 64 |
| Eau : maillage en mer (`WaterMaxLevel`) | 5 m | 5 m | 2,5 m | 1,2 m |

« Élevé » correspond au rendu d'avant l'ajout des niveaux.

## Pour le code : brancher une nouvelle couche

```csharp
// 1. Lire une valeur selon le niveau (le plus simple, pour toute nouvelle couche)
int waves = GraphicsQuality.Pick(16, 64, 128, 256);           // bas, moyen, élevé, ultra
double dist = 5_000 * GraphicsQuality.Current.DrawDistanceScale;
if (GraphicsQuality.Level >= QualityLevel.High) { /* effet coûteux */ }

// 2. Ombres : demander selon le type d'objet (Terrain, Trees, Buildings, Vehicles, Small)
mesh.CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Vehicles);

// 3. Réagir quand le joueur change de niveau (thread principal)
public override void _Ready()  { GraphicsQuality.Changed += OnQuality; OnQuality(GraphicsQuality.Current, null); }
public override void _ExitTree() => GraphicsQuality.Changed -= OnQuality;
void OnQuality(QualityProfile now, QualityProfile previous) { /* previous == null au premier appel */ }
```

Règles :

- **Threads** : `GraphicsQuality.Current` est un objet immuable. Dans un `Task.Run` (construction de chunks, de routes…),
  lire `var q = GraphicsQuality.Current;` **une fois** au début et utiliser `q` jusqu'au bout.
- **Toujours abonné** : une couche qui crée des objets doit se désabonner dans `_ExitTree`.
- **Pas de réglage en dur** : une distance, un nombre de particules, une taille de texture ou une option coûteuse
  passe par `Pick(...)`, `DrawDistanceScale` ou un champ de `QualityProfile`.
- **Un réglage partagé par plusieurs couches** (ex. qualité de l'eau utilisée par l'océan et les lacs) : ajouter un champ
  à `QualityProfile` et sa valeur pour les 4 niveaux dans `GraphicsQuality.Build`, puis une ligne au tableau ci-dessus.
- **Bas doit tourner sur une carte intégrée** (Intel UHD, Steam Deck) : en Bas, couper l'effet ou le rendre très léger.
- **Ultra peut être exigeant** : c'est pour les RTX 4070+ / RX 7800+.
- **Moteur de rendu** : en mode compatibilité OpenGL (`gl_compatibility`, vieilles cartes), SSAO, halo et FSR sont
  désactivés automatiquement. Une couche qui utilise une fonction propre à Forward+ doit vérifier
  `RenderingServer.GetCurrentRenderingMethod() == "forward_plus"`.

Ce qui est déjà branché : `Planet` (relief), `Vegetation` (arbres), `BuildingsLayer`, `RoadLayer`, `LabelTileLayer`
(noms des rues, arrêts), `Atmosphere` (nuages, pluie, neige), `GraphicsApplier` (anticrénelage, résolution, ombres
du soleil, SSAO, halo, vsync, FPS).

## Détection automatique

`GraphicsQuality.Classify(nom, type, moteur)` reconnaît les cartes les plus courantes sur Steam d'après leur nom
(NVIDIA GTX/RTX, AMD RX et Radeon intégrées, Intel UHD/Iris/Arc, Apple M) ; une carte inconnue est classée selon son
type (dédiée → Moyen, intégrée → Bas). Le niveau est ensuite limité à Moyen avec moins de 8 Go de mémoire ou moins de
4 cœurs. Pour une nouvelle génération de cartes, ajouter son nom dans `Classify`.

## Tests automatiques

`-- --quality=0` à `--quality=3` impose un niveau pour ce lancement, sans toucher aux réglages du joueur :

```
godot --path . -- --view=1 --quality=3 --shot=ultra.png
```
