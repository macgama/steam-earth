using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

public enum BodyType { Male, Female }

/// <summary>
/// Ressources communes à tous les personnages (joueur et futurs passants) : corps de base, coiffures et
/// bibliothèque d'animations. Tout vient des packs gratuits CC0 de Quaternius (voir assets/characters/quaternius/),
/// qui partagent le même squelette de 65 os : un seul jeu d'animations sert à tous les corps.
/// Les ressources sont chargées une fois puis partagées (cache).
/// </summary>
public static class CharacterAssets
{
    const string Root = "res://assets/characters/quaternius/";

    public static string BodyPath(BodyType b) => Root + (b == BodyType.Male ? "base/Superhero_Male_FullBody.gltf" : "base/Superhero_Female_FullBody.gltf");

    /// <summary>Coiffures (identifiant → fichier). « none » = crâne rasé.</summary>
    public static readonly (string Id, string Name, string File)[] Hairs =
    {
        ("short", "Courts, raie sur le côté", "Hair_SimpleParted"),
        ("buzzed", "Très courts", "Hair_Buzzed"),
        ("buzzed_f", "Très courts (fins)", "Hair_BuzzedFemale"),
        ("long", "Longs", "Hair_Long"),
        ("buns", "Chignons", "Hair_Buns"),
        ("none", "Aucun", null),
    };

    public static string HairPath(string file) => Root + "hair/" + file + ".gltf";
    public const string BeardFile = "Hair_Beard";
    public static string EyebrowsFile(BodyType b) => b == BodyType.Male ? "Eyebrows_Regular" : "Eyebrows_Female";

    static readonly Dictionary<string, PackedScene> _scenes = new();

    public static PackedScene Scene(string path)
    {
        if (!_scenes.TryGetValue(path, out var s))
        {
            s = ResourceLoader.Load<PackedScene>(path);
            _scenes[path] = s;
        }
        return s;
    }

    // ---------------------------------------------------------------- animations

    /// <summary>Fichiers d'animations (Universal Animation Library 1 et 2 de Quaternius, CC0).</summary>
    static readonly string[] AnimationFiles = { Root + "animations/UAL1.glb", Root + "animations/UAL2.glb" };

    static List<Animation> _source;            // animations d'origine (squelette du mannequin)
    static Dictionary<string, Quaternion> _srcRest; // rotation de repos de chaque os du mannequin
    static Vector3 _srcPelvis;                 // position de repos du bassin du mannequin
    static readonly Dictionary<BodyType, AnimationLibrary> _libraries = new();

    /// <summary>
    /// Bibliothèque d'animations adaptée à un corps : les corps n'ont pas exactement la même pose de repos que
    /// le mannequin des animations (cou, épaules, pieds diffèrent de quelques degrés), on convertit donc chaque
    /// rotation en « écart par rapport au repos » puis on l'applique au repos du corps. La hauteur du bassin est
    /// mise à l'échelle de la taille du corps. Les autres pistes de position sont retirées (les os gardent la longueur
    /// de leur corps).
    /// </summary>
    public static AnimationLibrary Library(BodyType body, Skeleton3D target)
    {
        if (_libraries.TryGetValue(body, out var lib)) return lib;
        LoadSource();
        lib = new AnimationLibrary();
        var rest = new Dictionary<string, Quaternion>();
        for (int i = 0; i < target.GetBoneCount(); i++) rest[target.GetBoneName(i)] = target.GetBoneRest(i).Basis.GetRotationQuaternion();
        int pelvis = target.FindBone("pelvis");
        var tgtPelvis = target.GetBoneRest(pelvis).Origin;
        float scale = tgtPelvis.Y / Math.Max(0.01f, _srcPelvis.Y);

        foreach (var src in _source)
        {
            var a = new Animation { Length = src.Length, LoopMode = src.LoopMode, Step = src.Step };
            for (int t = 0; t < src.GetTrackCount(); t++)
            {
                var path = src.TrackGetPath(t);
                string bone = path.GetSubNameCount() > 0 ? path.GetSubName(0) : "";
                var type = src.TrackGetType(t);
                if (type == Animation.TrackType.Rotation3D)
                {
                    if (!rest.TryGetValue(bone, out var rTgt) || !_srcRest.TryGetValue(bone, out var rSrc)) continue;
                    int nt = a.AddTrack(type);
                    a.TrackSetPath(nt, path);
                    a.TrackSetInterpolationType(nt, src.TrackGetInterpolationType(t));
                    var inv = rSrc.Inverse();
                    for (int k = 0; k < src.TrackGetKeyCount(t); k++)
                    {
                        var q = (Quaternion)src.TrackGetKeyValue(t, k);
                        a.RotationTrackInsertKey(nt, src.TrackGetKeyTime(t, k), (rTgt * (inv * q)).Normalized());
                    }
                }
                else if (type == Animation.TrackType.Position3D && bone == "pelvis")
                {
                    int nt = a.AddTrack(type);
                    a.TrackSetPath(nt, path);
                    a.TrackSetInterpolationType(nt, src.TrackGetInterpolationType(t));
                    for (int k = 0; k < src.TrackGetKeyCount(t); k++)
                    {
                        var p = (Vector3)src.TrackGetKeyValue(t, k);
                        a.PositionTrackInsertKey(nt, src.TrackGetKeyTime(t, k), tgtPelvis + (p - _srcPelvis) * scale);
                    }
                }
            }
            lib.AddAnimation(src.ResourceName, a);
        }
        _libraries[body] = lib;
        return lib;
    }

    static void LoadSource()
    {
        if (_source != null) return;
        _source = new List<Animation>();
        _srcRest = new Dictionary<string, Quaternion>();
        var seen = new HashSet<string>();
        foreach (var file in AnimationFiles)
        {
            var scene = Scene(file);
            if (scene == null) { GD.PushWarning($"[Personnage] animations introuvables : {file}"); continue; }
            var inst = scene.Instantiate<Node3D>();
            var skel = inst.FindChild("Skeleton3D", true, false) as Skeleton3D;
            if (skel != null && _srcRest.Count == 0)
            {
                for (int i = 0; i < skel.GetBoneCount(); i++) _srcRest[skel.GetBoneName(i)] = skel.GetBoneRest(i).Basis.GetRotationQuaternion();
                _srcPelvis = skel.GetBoneRest(skel.FindBone("pelvis")).Origin;
            }
            if (inst.FindChild("AnimationPlayer", true, false) is AnimationPlayer ap)
            {
                foreach (string name in ap.GetAnimationList())
                {
                    if (!seen.Add(name)) continue;
                    var anim = ap.GetAnimation(name);
                    anim.ResourceName = name;
                    _source.Add(anim);
                }
            }
            inst.Free();
        }
    }
}
