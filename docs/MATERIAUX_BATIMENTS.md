# Matières des façades et des toits

Les bâtiments n'utilisent aucune image : crépi, bois, pierre, tuiles… sont dessinés par le shader des bâtiments
(`scripts/Buildings/BuildingShader.cs`) d'après trois informations portées par chaque sommet. Tout maillage qui
respecte ces conventions reçoit les mêmes textures, les mêmes fenêtres et le même éclairage de nuit, y compris les
modèles 3D importés (swissBUILDINGS3D).

## Conventions des sommets

| Donnée | Contenu |
|---|---|
| `COLOR.rgb` | couleur de base (sRGB) : teinte du crépi, du bois, des tuiles… |
| `COLOR.a` | `1 − code / 255`. Code = matière (bits 0–5, enum `Surface`) + 64 si les fenêtres ont des volets. `1` = uni (aspect d'avant) |
| `UV` | **en mètres**. Murs : (abscisse le long du mur, hauteur au-dessus du sol du rez-de-chaussée ; 3 unités par étage). Toits et faces quelconques : `FacadeMaterials.SlopeUv` (u horizontal le long du pan, v vers le bas de la pente) |
| `UV2` | (motif de fenêtres `WindowStyle`, aléa 0–1 propre au bâtiment). Motif 0 = pas de fenêtres |

Côté C# : `MeshAcc.Material = code` avant d'ajouter des sommets (l'alpha est rempli tout seul), ou
`FacadeMaterials.Encode(couleur, code)`. Le code d'un mur avec volets : `FacadeMaterials.Code(Surface.Render, true)`.

## Matières (`enum Surface`)

| Code | Matière | Remarques |
|---|---|---|
| 1 | Crépi | teinte qui varie doucement, grain fin |
| 2 | Bois (madriers, planches) | joints sombres tous les 20 cm, aboutements |
| 3 | Moellons | rangées irrégulières, joints de mortier |
| 4 | Brique | appareil en panneresses |
| 5 | Béton banché | panneaux de 2,4 × 1,2 m, trous de banches |
| 6 | Bardage métallique ondulé | halles, entrepôts |
| 7 | Chalet | sous `UV.y = 3` (rez-de-chaussée, socle) : crépi blanc ; au-dessus : bois (couleur des sommets = bois) |
| 8 | Colombages | crépi clair, poteaux, sablières et écharpes sombres |
| 9 | Clins peints | Amérique du Nord, Scandinavie |
| 16 | Tuiles | à côtes ou en queue de castor (selon l'aléa) |
| 17 | Ardoise / fibrociment | rectangles décalés ou losanges |
| 18 | Tôle à joints debout | |
| 19 | Tavillons / bardeaux | bois gris argenté, ou asphalte selon la couleur |
| 20 | Gravier | toits plats |
| 21 | Cuivre | vert-de-gris |
| 22 | Toit végétalisé | |
| 23 | Lauzes | dalles de pierre (Tessin, Valais) |
| 30 | Balustrade en bois | planches découpées (chalets), les vides sont percés |
| 31 | Garde-corps à barreaux | |
| 32 | Plancher de balcon | |

## Choix automatique

`FacadeMaterials.Choose` décide quand OSM ne dit rien (`facade_material`, `roof_material` gardent la priorité) :

- **Alpes** (au-dessus de 650 m) : plus on monte, plus il y a de chalets ; toits en ardoise/fibrociment, tôle,
  tavillons ou tuiles ; balcons en bois.
- **Tessin, Valais du Sud** : maisons en pierre et toits en lauzes pour les petites maisons.
- **Plateau suisse, Jura** : crépi, quelques maisons en pierre, colombages et fermes-chalets ; toits plats parfois
  végétalisés.
- **Europe du Nord-Ouest** : brique. **Scandinavie** : clins peints. **Amérique du Nord** : clins, brique, bardeaux
  d'asphalte. **Méditerranée** : crépi et tuiles.
- Halles et entrepôts : bardage métallique ou béton ; églises : pierre ou crépi.
- Volets : surtout en Suisse, en France et en Italie (une couleur par maison, certains fermés, davantage la nuit).
- Balcons : chalets (bois), immeubles (garde-corps métalliques alignés sur les fenêtres), quelques maisons.

## Niveaux de qualité (`QualityProfile.BuildingDetail`)

| | Bas | Moyen | Élevé | Ultra |
|---|---|---|---|---|
| Motifs (joints, rangées de tuiles, planches) | oui | oui | oui | oui |
| Bruit (variations de teinte, grain) | non | 2 octaves | 3 octaves | 4 octaves |
| Cadres de fenêtres, tablettes, volets | non | oui | oui | oui |
| Balcons (géométrie) | non | oui | oui | oui |
| Relief des joints, des tuiles (normale perturbée) | non | non | oui (à l'écran) | oui (différences finies, plus net) |
| Salissures au pied des murs / lichens des lauzes | non | non | oui | oui |
| Coulures de pluie, mousse sur les toits | non | non | non | oui |

Au loin, chaque motif se fond dans sa teinte moyenne dès qu'il devient plus fin qu'un pixel (pas de moiré).
