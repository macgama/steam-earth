# Passants : les vrais habitants de la Suisse

En Suisse, les rues se remplissent selon **le vrai nombre d'habitants de chaque hectare** : beaucoup de monde dans la
vieille ville de Berne à midi, deux ou trois personnes dans un hameau, presque personne à 3 h du matin ou sous une
grosse averse. Chaque passant est un **habitant imaginaire mais plausible** de l'hectare où il se promène, avec son
nom, son âge, son origine, son ménage et son activité, toujours les mêmes.

## Ce qu'on voit en jeu

- Autour du joueur (à pied, mais aussi en voiture, à vélo… tant qu'on est près du sol en Suisse), des passants
  marchent sur **les trottoirs** (au bord des rues, des deux côtés) et **au milieu des chemins, zones piétonnes et
  escaliers**. Ils tournent aux carrefours, traversent parfois, font demi-tour dans les impasses, s'arrêtent pour
  téléphoner ou attendre, et s'arrêtent si le joueur leur barre la route.
- **Les enfants de moins de 12 ans sortent avec un parent**, à côté de lui. Les couples se promènent souvent ensemble.
- Un passant **joggeur** de temps en temps (4 % des 18-55 ans).
- **Touche I** : la fiche du passant le plus proche (à moins de 8 m). Il s'arrête, se tourne vers toi et te salue
  dans sa langue (« Grüezi! », « Bonjour ! », « Buongiorno! », « Allegra! »). La fiche :
  - prénom, nom, âge ;
  - nationalité (Suisse avec son lieu d'origine, naturalisé, ou étranger avec l'année d'arrivée / né en Suisse) ;
  - commune où il habite et avec qui (« avec Marc (conjoint, 37 ans), Noah (fils, 8 ans) et Mia (fille, 5 ans) ») ;
  - activité : crèche, école enfantine, primaire (année), secondaire, gymnase, apprentissage CFC, université la plus
    proche, métier et lieu de travail (souvent la grande ville voisine), temps partiel, retraite et ancien métier ;
  - langue parlée et deux loisirs.
- En passant tout près de quelqu'un à pied, un rappel s'affiche en bas : « I : parler à Lena ».
- **F3** : nombre de passants affichés, voulus (avant la limite de la qualité graphique), et de trottoirs chargés.

## D'où viennent les chiffres

| Donnée | Source | Licence |
|---|---|---|
| Habitants par hectare, âge par 5 ans et sexe, nationalité (Suisse, UE/AELE, reste de l'Europe, hors Europe), taille des ménages | **STATPOP 2025**, Office fédéral de la statistique (OFS), via data.geo.admin.ch | OGD, usage commercial autorisé avec la source (« terms_by » d'opendata.swiss) |
| Communes (nom, canton, district, habitants), pour la commune de chaque hectare | **swissBOUNDARIES3D 2026**, swisstopo | OGD, usage commercial autorisé avec « © swisstopo » |

Les deux sont citées dans les Crédits. Fichier du jeu : `data/population/statpop_ch.bin.gz` (1,4 Mo, 346 000 hectares
habités, 9,08 millions d'habitants, 2 110 communes), construit par `tools/population/build_population.py` (voir l'en-tête
du script pour les liens de téléchargement et le format).

Pour la protection des données, l'OFS remplace les valeurs 1 et 2 par 3 : les âges, nationalités et ménages sont donc
additionnés par **carré de 1 km** (profil du quartier) ; seul le **nombre d'habitants** est pris hectare par hectare.

### Ce qui est inventé (mais tiré des vrais chiffres)

- **Ménages** : tailles tirées du carré de 1 km ; un ménage de 2 est un couple (parfois une famille monoparentale ou
  une colocation), de 3 et plus une famille (parfois avec un grand-parent). Âges des enfants cohérents avec les parents.
- **Langue** : celle de la commune (cantons, districts et communes bilingues : Bienne, Fribourg, Morat, Sierre,
  Haut-Valais, Jura bernois, Singine, vallées italophones et romanches des Grisons…).
- **Pays** des étrangers : selon le groupe STATPOP de l'hectare, puis selon les effectifs nationaux approximatifs
  (Italie, Allemagne, Portugal, France, Kosovo, Espagne, Turquie…), corrigés par région (plus de Français et de
  Portugais en Suisse romande, d'Italiens au Tessin). 16 % des familles suisses ont une origine étrangère.
- **Prénoms** : à la mode de l'année de naissance (Ursula et Hans, Sandra et Daniel, Mia et Noah…) dans la langue de la
  région, ou du pays d'origine. **Noms** courants de la région ou du pays.
- **Métiers** : une cinquantaine, pondérés à peu près comme en Suisse (plus d'agriculteurs dans les petites communes,
  de moniteurs de ski et de guides en montagne), temps partiel fréquent. Lieu de travail : la commune, ou la ville de
  plus de 15 000 habitants la plus proche.
- **Apparence** : sexe, taille des enfants, cheveux gris avec l'âge, teinte de peau plausible selon l'origine, tenue
  selon la température réelle (hiver, été) et le métier (chantier), couleurs au hasard.

## Qui est dehors, et quand

`PopulationLayer.OutdoorRate` : part des habitants dehors près de chez eux, heure par heure (0,1 % la nuit, 2,6 % à
8 h, 3,6 % à midi, 4 % à 17 h), plus le week-end en journée, élèves rares pendant les heures de classe, aînés surtout
en journée. Pluie : × 0,45 (forte pluie × 0,25), grand froid × 0,7, vent fort × 0,6 (météo réelle GFS). Le tirage est
fixé par **quart d'heure** : une personne reste dehors un moment puis rentre. On ne fait apparaître ni disparaître
personne sous les yeux du joueur (à moins de 35 m dans le champ de vision).

## Niveaux graphiques

| | Bas | Moyen | Élevé | Ultra |
|---|---|---|---|---|
| Passants à la fois (au plus) | 12 | 30 | 60 | 120 |
| Rayon des hectares pris en compte | 90 m | 130 m | 170 m | 230 m |
| Animés à chaque image jusqu'à (au-delà : 15 puis 6 images/s) | 15 m | 30 m | 50 m | 90 m |

Les passants les plus proches passent en premier. Ombres : celles des personnages (`ShadowCaster.Characters`).

## Pour le code

| Fichier | Rôle |
|---|---|
| `scripts/Population/PopulationData.cs` | lecture du fichier STATPOP : hectares, carrés de 1 km, communes ; LV95 ↔ WGS84 |
| `scripts/Population/Residents.cs` | habitants d'un hectare (ménages, âges, nationalités, métiers), en cache ; métiers et loisirs |
| `scripts/Population/Names.cs` | prénoms (par langue et génération), noms, pays d'origine et leurs poids |
| `scripts/Population/Story.cs` | texte de la fiche (français / anglais), liens de parenté, salutations |
| `scripts/Population/Looks.cs` | apparence (`CharacterAppearance`) d'un habitant |
| `scripts/Population/Walkways.cs` | trottoirs et chemins, remplis par `RoadBuilder` / `RoadLayer` (zoom 14) |
| `scripts/Population/Pedestrian.cs` | un passant : marche le long des trottoirs, pauses, carrefours, suit son parent |
| `scripts/Population/PopulationLayer.cs` | qui est dehors, apparitions / disparitions, touche I |
| `scripts/Population/PersonCard.cs` | la fiche à l'écran |

Les passants utilisent le personnage commun (`CharacterModel`, voir `docs/PERSONNAGE.md`) ; à l'arrêt ils jouent
`CharacterMotion.Idle` (« Idle_TalkingPhone », « Idle_FoldArms », « Idle_Talking »).

## Tests

```
# vieille ville de Berne, à pied, fiche du passant le plus proche, capture (texte de la fiche dans le journal)
godot --path . -- --at=46.9479,7.4490,2,250,-5 --walk --talk --shot=berne.png
```

## Pistes

- Gens qui ne dorment pas là où ils sont (pendulaires, touristes) : nombre d'emplois par hectare (STATENT, OFS).
- Passants dans les bâtiments (à la maison, au travail), dans les trains et bus, aux arrêts.
- Réactions au joueur (saluer en passant, s'écarter), discussions.
