# Personnage : corps, tenues, animations

Le joueur (et plus tard les passants) est un **personnage 3D stylisé**, animé, habillé selon l'activité.
Tout est gratuit et utilisable dans un jeu vendu sur Steam.

## Ce qu'on voit en jeu

- **Touche U** (ou « Personnage » dans l'écran titre / le menu pause) : choisir qui on incarne. 6 personnages prêts
  (Alex, Léa, Malik, Yuna, Sven, Amara), puis tout se règle : homme / femme, taille, corpulence, couleur de peau,
  coiffure et couleur des cheveux, barbe, tenue et couleur de chaque pièce. Aperçu 3D qui tourne, gestes (marche,
  course, salut, danse). Le choix est gardé dans `user://settings.cfg`, section `[personnage]`.
- **Tenue auto** (cochée par défaut) : la tenue suit l'activité. Vélo → casque, maillot, cuissard ; avion → blouson
  d'aviateur ; bateau → gilet de sauvetage ; dans l'eau → maillot de bain. À pied ou en voiture : la tenue choisie.
- **À pied** : marche (X : marcher tranquillement / trottiner), course (Maj), saut, réception, accroupi (C),
  escalade (Espace contre un mur), **nage** dans les lacs, les rivières et la mer (Maj : crawl plus rapide).
  La tête suit le regard de la caméra.
- **Véhicules** : le conducteur est le personnage du joueur, assis avec les mains sur le volant ; à vélo il pédale
  au rythme du pédalier et se penche vers le guidon.
- **Vue à la première personne (V)** : la caméra est dans les yeux du personnage (elle suit l'animation : course,
  nage, accroupi). En baissant les yeux on voit son corps, ses bras et ses jambes ; la tête ne fait plus que son ombre.

## D'où viennent les modèles

`assets/characters/quaternius/` — packs gratuits **CC0** (domaine public, usage commercial libre) de Quaternius :

| Dossier | Pack | Contenu |
|---|---|---|
| `base/` | Universal Base Characters (Standard) | corps homme et femme, yeux, sourcils, textures |
| `hair/` | Universal Base Characters, coiffures « rigged » | 6 coiffures + barbe, sourcils |
| `animations/UAL1.glb` | Universal Animation Library 1 | 45 animations : marche, course, sprint, saut, nage, accroupi, conduite, assis, gestes… |
| `animations/UAL2.glb` | Universal Animation Library 2 | 43 animations : escalade d'un rebord, glissade, porter, téléphoner, récolter… |

Tous partagent **le même squelette de 65 os** : un seul jeu d'animations sert à tous les corps.

Les **vêtements ne sont pas des modèles téléchargés** : ils sont générés par le jeu à partir de la peau du corps
(voir plus bas). Il n'existe pas de vêtements modernes gratuits pour ce squelette ; les générer donne en plus des
couleurs libres et autant de pièces qu'on veut.

## Pour le code

| Fichier | Rôle |
|---|---|
| `scripts/Characters/CharacterModel.cs` | **le personnage** (nœud 3D) : construit corps + vêtements + coiffure + accessoires, choisit l'animation, ajoute les mouvements calculés (regard, pédalage, escalade). Commun au joueur et aux futurs passants. |
| `scripts/Characters/CharacterAppearance.cs` | apparence (corps, peau, cheveux, tenue, couleurs) ; personnages prêts ; `Random(seed)` pour les passants ; `PlayerAppearance` = apparence et activité du joueur |
| `scripts/Characters/Garments.cs` | catalogue des vêtements (zone couverte, épaisseur, tissu) et des 11 tenues par activité (`GameMode`) |
| `scripts/Characters/CharacterMeshes.cs` | génération des vêtements et de la peau visible (cache partagé) |
| `scripts/Characters/CharacterAssets.cs` | chargement des modèles et adaptation des animations à chaque corps |
| `scripts/Characters/CharacterMenu.cs` | menu Personnage (aperçu 3D) |
| `scripts/Characters/CharacterPreview.cs` + `scenes/CharacterPreview.tscn` | studio de test (sans la Terre) |
| `scripts/Character.cs` | le joueur à pied : physique (gravité radiale, nage, escalade) → `CharacterModel` |

Utiliser un personnage ailleurs (passant, figurant dans un train…) :

```csharp
var npc = new CharacterModel();
AddChild(npc);
npc.SetAppearance(CharacterAppearance.Random(seed), GameMode.City);
npc.UpdateInterval = distance > 40 ? 0.1 : 0;     // animation moins souvent quand il est loin
// chaque image :
npc.Update(new CharacterMotion { Speed = 1.3, Grounded = true }, delta);
npc.PlayAction("Interact");                        // geste : porte, « PickUp_Table », « Yes », « Dance »…
```

L'avant du nœud est −Z, l'origine entre les pieds. `EyeLocal` = position des yeux, `PelvisLocal` = bassin
(pour asseoir le personnage sur un siège).

### Comment les vêtements sont générés

Pour chaque vêtement, `Garments.cs` donne la **zone couverte** en coordonnées du corps au repos (bras 0 = épaule →
1 = poignet ; jambe 0 = hanche → 1 = cheville ; tronc 0 = hanche → 1 = cou ; tête 0 = cou → 1 = sommet), son
**épaisseur** et son tissu. `CharacterMeshes` :

1. affine la silhouette selon la corpulence (le corps d'origine est très musclé) ;
2. découpe la peau exactement le long de l'ourlet (les triangles sont coupés, pas arrondis au triangle près) ;
3. pousse la partie couverte vers l'extérieur de l'épaisseur, la **lisse** (plus de muscles sous une veste, plus
   d'orteils dans une chaussure), recalcule les normales ;
4. ferme le bord par un ourlet, assombrit les bords (poignets, col, taille), dessine la fermeture des vestes,
   éclaircit les semelles ;
5. retire la peau cachée (découpée le long du même bord : pas de jour, rien ne transperce).

Les os et les poids sont ceux de la peau : un vêtement suit toutes les animations sans réglage.
Ajouter un vêtement = une ligne dans `Garments.All` ; une tenue = une entrée dans `Garments.Outfits`.

### Animations

Les animations viennent du mannequin de Quaternius, dont la pose de repos diffère un peu de celle des corps (cou,
épaules, pieds). `CharacterAssets.Library` convertit chaque rotation en « écart au repos » puis l'applique au repos du
corps, et met la hauteur du bassin à l'échelle. Les animations sont avancées à la main (`Update`), ce qui permet
d'ajouter ensuite les mouvements calculés (`Procedural`) et de ralentir les passants lointains (`UpdateInterval`).

## Niveaux graphiques

| | Bas | Moyen | Élevé | Ultra |
|---|---|---|---|---|
| Ombre du personnage | non | oui | oui | oui |
| Ourlets (bord des vêtements fermé) | non | oui | oui | oui |
| Relief de la peau et des cheveux (normal maps), tissage des vêtements | non | oui | oui | oui |
| Reflets satinés des tissus et contour de la peau | non | non | oui | oui |
| Peau translucide (oreilles, doigts au soleil), reflets étirés des cheveux | non | non | non | oui |

## Tests

```
# studio : personnages côte à côte, une animation, capture
godot --path . res://scenes/CharacterPreview.tscn -- --set=presets --anim=Walk --cam=front --shot=f.png
#   --set=presets|outfits|bodies|one   --only=0,2,5   --anim=Idle|Walk|Jog|Sprint|swim|climb|bike|drive|sit|crouch|jump|Dance…
#   --cam=front|side|back|close|hips|feet   --time=0.6   --quality=0..3
# en jeu : menu Personnage ouvert
godot --path . -- --view=1 --character --shot=menu.png
```
