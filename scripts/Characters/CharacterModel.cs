using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>Où le personnage est assis (pose et animation adaptées).</summary>
public enum CharacterSeat { None, Car, Bike, Boat, Plane, Passenger }

/// <summary>État du mouvement, fourni chaque image par celui qui contrôle le personnage (joueur ou futur passant).</summary>
public struct CharacterMotion
{
    public double Speed;          // vitesse horizontale (m/s)
    public bool Grounded;
    public double VerticalSpeed;  // m/s, + = monte
    public bool Climbing, Swimming, Crouching;
    public CharacterSeat Seat;
    public double LookPitch;      // radians, + = regarde vers le haut (tête et cou suivent)
    public double Pedal;          // vélo : angle du pédalier (radians)
    public double Lean;           // vélo, virages : inclinaison du buste (radians)
    public string Idle;           // animation à l'arrêt à la place de « Idle » (passants : « Idle_Talking », « Idle_TalkingPhone »…)
}

/// <summary>
/// Personnage 3D animé, commun au joueur et aux futurs passants : corps CC0 (homme ou femme), peau teintée,
/// coiffure, vêtements générés (<see cref="CharacterMeshes"/>), accessoires (sac à dos, visière) et animations
/// (marche, course, saut, nage, escalade, assis en véhicule, pédalage, gestes).
/// L'avant du nœud est −Z, le haut +Y, l'origine entre les pieds (comme l'ancien mannequin).
/// </summary>
public partial class CharacterModel : Node3D
{
    public CharacterAppearance Appearance { get; private set; }
    public GameMode Mode { get; private set; } = GameMode.Explore;
    /// <summary>Suit l'apparence du joueur (menu Personnage, tenue auto) : se reconstruit quand elle change.</summary>
    public bool IsPlayer;
    /// <summary>Animation mise à jour au plus toutes les N secondes (passants lointains) ; 0 = chaque image.</summary>
    public double UpdateInterval;

    Node3D _visual, _inst;
    Skeleton3D _skel;
    AnimationPlayer _player;
    readonly List<GeometryInstance3D> _headParts = new(), _allParts = new();
    MeshInstance3D _skinSource;
    bool _firstPerson;
    string _anim = "";
    string _action;          // geste en cours (porte, ramasser…), null sinon
    double _airTime, _sinceUpdate, _landTimer;
    int _boneNeck = -1, _boneHead, _bonePelvis, _boneSpine1, _boneSpine3, _boneThighL, _boneThighR, _boneCalfL, _boneCalfR,
        _boneUpperL, _boneUpperR, _boneLowerL, _boneLowerR, _boneFootL, _boneFootR;
    Vector3 _eyeInHead;      // position des yeux dans le repère de l'os de la tête
    CharacterSeat _seat;

    /// <summary>Vitesses naturelles des animations (m/s), pour que les pieds ne glissent pas.</summary>
    const float WalkAnimSpeed = 1.45f, JogAnimSpeed = 3.6f, SprintAnimSpeed = 6.2f, CrouchAnimSpeed = 1.1f, SwimAnimSpeed = 1.2f;

    public override void _Ready()
    {
        _visual ??= new Node3D { Name = "Visual", Rotation = new Vector3(0, Mathf.Pi, 0) };
        if (_visual.GetParent() == null) AddChild(_visual);
        GraphicsQuality.Changed += OnQuality;
        if (IsPlayer) PlayerAppearance.Changed += OnPlayerChanged;
        // apparence par défaut, si personne ne l'a donnée juste après l'ajout du nœud
        if (Appearance == null) CallDeferred(MethodName.DefaultAppearance);
    }

    void DefaultAppearance()
    {
        if (Appearance == null) SetAppearance(IsPlayer ? PlayerAppearance.Current : CharacterAppearance.Presets()[0], IsPlayer ? PlayerAppearance.Mode : Mode);
    }

    public override void _ExitTree()
    {
        GraphicsQuality.Changed -= OnQuality;
        if (IsPlayer) PlayerAppearance.Changed -= OnPlayerChanged;
    }

    void OnPlayerChanged() => SetAppearance(PlayerAppearance.Current, PlayerAppearance.Mode);

    void OnQuality(QualityProfile now, QualityProfile previous)
    {
        if (Appearance != null) SetAppearance(Appearance, Mode);
    }

    /// <summary>Habille le personnage (reconstruit le modèle ; l'animation en cours continue).</summary>
    public void SetAppearance(CharacterAppearance a, GameMode mode)
    {
        Appearance = a.Clone();
        Mode = mode;
        if (_visual == null) { _visual = new Node3D { Name = "Visual", Rotation = new Vector3(0, Mathf.Pi, 0) }; AddChild(_visual); }
        Build();
    }

    void Build()
    {
        var q = GraphicsQuality.Current;
        var a = Appearance;
        if (_inst != null) { _visual.RemoveChild(_inst); _inst.QueueFree(); }
        _headParts.Clear(); _allParts.Clear();
        _anim = "";

        var scene = CharacterAssets.Scene(CharacterAssets.BodyPath(a.Body));
        if (scene == null) { GD.PushWarning("[Personnage] corps introuvable"); return; }
        _inst = scene.Instantiate<Node3D>();
        _visual.AddChild(_inst);
        _visual.Scale = Vector3.One * a.Height;
        _skel = _inst.FindChild("Skeleton3D", true, false) as Skeleton3D;
        if (_skel == null) return;
        CacheBones();

        // Maillages d'origine : peau (le plus gros), yeux, sourcils
        var meshes = _skel.GetChildren().OfType<MeshInstance3D>().ToList();
        _skinSource = meshes.OrderByDescending(m => m.Mesh.GetFaces().Length).First();
        var skinMat = SkinMaterial(_skinSource.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D, a.Skin, q);
        foreach (var m in meshes)
        {
            if (m == _skinSource) { m.Visible = false; continue; }
            var mat = m.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D;
            bool brows = mat != null && mat.ResourceName.StartsWith("MI_Hair");
            if (brows) m.MaterialOverride = HairMaterial(mat, a.HairColor, q);
            Track(m, head: true);
        }

        // Peau visible + vêtements (générés)
        var garments = a.GarmentsFor(Mode).Select(g => (g, def: Garments.Get(g.Id))).Where(x => x.def != null).ToList();
        var built = CharacterMeshes.Build(a.Body, a.Build, _skinSource.Mesh, _skel, garments.Select(x => x.def), hems: q.Level >= QualityLevel.Medium);
        AddSkinned(built.Body, skinMat, head: false);
        AddSkinned(built.Head, skinMat, head: true);
        var colorOf = garments.ToDictionary(x => x.def.Id, x => x.g.Color);
        if (built.Clothes != null)
        {
            var mi = AddSkinned(built.Clothes, null, head: false);
            for (int i = 0; i < built.ClothesDefs.Length; i++) mi.SetSurfaceOverrideMaterial(i, GarmentMaterial(built.ClothesDefs[i], colorOf[built.ClothesDefs[i].Id], q));
        }
        if (built.Headwear != null)
        {
            var mi = AddSkinned(built.Headwear, null, head: true);
            for (int i = 0; i < built.HeadwearDefs.Length; i++) mi.SetSurfaceOverrideMaterial(i, GarmentMaterial(built.HeadwearDefs[i], colorOf[built.HeadwearDefs[i].Id], q));
        }

        // Coiffure (cachée sous un bonnet ou un casque), barbe
        bool hatted = garments.Any(x => x.def.HidesHair);
        var hair = CharacterAssets.Hairs.FirstOrDefault(h => h.Id == a.Hair).File;
        if (hair != null && !hatted) AddHair(CharacterAssets.HairPath(hair), a.HairColor, q);
        if (a.Beard && a.Body == BodyType.Male) AddHair(CharacterAssets.HairPath(CharacterAssets.BeardFile), a.HairColor, q);

        // Accessoires
        foreach (var (g, def) in garments)
        {
            if (def.Prop == GarmentProp.Backpack) AddBackpack(g.Color, q, garments.Where(x => x.def.Slot is GarmentSlot.Outer or GarmentSlot.Top).Select(x => x.def.Thickness).DefaultIfEmpty(0).Max());
            if (def.Prop == GarmentProp.CapBrim) AddBrim(g.Color, q);
        }

        // Animations
        _player = new AnimationPlayer { Name = "AnimationPlayer", CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual };
        _inst.AddChild(_player);
        _player.AddAnimationLibrary("", CharacterAssets.Library(a.Body, _skel));
        Play("Idle", 0);
        _player.Advance(0);

        ApplyShadows();
        ApplyFirstPerson();
    }

    void CacheBones()
    {
        int B(string n) => _skel.FindBone(n);
        _boneNeck = B("neck_01"); _boneHead = B("Head"); _bonePelvis = B("pelvis"); _boneSpine1 = B("spine_01"); _boneSpine3 = B("spine_03");
        _boneThighL = B("thigh_l"); _boneThighR = B("thigh_r"); _boneCalfL = B("calf_l"); _boneCalfR = B("calf_r");
        _boneUpperL = B("upperarm_l"); _boneUpperR = B("upperarm_r"); _boneLowerL = B("lowerarm_l"); _boneLowerR = B("lowerarm_r");
        _boneFootL = B("foot_l"); _boneFootR = B("foot_r");
        var head = _skel.GetBoneGlobalRest(_boneHead);
        // yeux : au-dessus de l'os de la tête et un peu en avant du visage (la caméra ne voit pas les paupières)
        _eyeInHead = head.AffineInverse() * (head.Origin + new Vector3(0, 0.1f, 0.13f));
    }

    MeshInstance3D AddSkinned(ArrayMesh mesh, Material mat, bool head)
    {
        if (mesh == null) return null;
        var mi = new MeshInstance3D { Mesh = mesh, Skin = _skinSource.Skin, MaterialOverride = mat };
        _skel.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(_skel);
        Track(mi, head);
        return mi;
    }

    void AddHair(string path, Color color, QualityProfile q)
    {
        var scene = CharacterAssets.Scene(path);
        if (scene == null) return;
        var tmp = scene.Instantiate<Node3D>();
        foreach (var src in tmp.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            var mi = new MeshInstance3D { Mesh = src.Mesh, Skin = src.Skin };
            var mat = src.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D;
            mi.MaterialOverride = HairMaterial(mat, color, q);
            _skel.AddChild(mi);
            mi.Skeleton = mi.GetPathTo(_skel);
            Track(mi, head: true);
        }
        tmp.Free();
    }

    void Track(GeometryInstance3D g, bool head)
    {
        _allParts.Add(g);
        if (head) _headParts.Add(g);
    }

    /// <summary>Accessoire rigide accroché à un os (suit ses mouvements). <paramref name="skelSpace"/> = position au repos, repère du squelette.</summary>
    BoneAttachment3D Attach(int bone, Transform3D skelSpace)
    {
        var at = new BoneAttachment3D { BoneName = _skel.GetBoneName(bone) };
        _skel.AddChild(at);
        var holder = new Node3D { Transform = _skel.GetBoneGlobalRest(bone).AffineInverse() * skelSpace };
        at.AddChild(holder);
        return at;
    }

    void AddBackpack(Color color, QualityProfile q, float clothes)
    {
        var spine = _skel.GetBoneGlobalRest(_boneSpine3).Origin;
        float back = spine.Z - 0.115f - clothes; // dos (le personnage regarde vers +Z dans le squelette)
        var at = Attach(_boneSpine3, new Transform3D(Basis.Identity, new Vector3(0, spine.Y - 0.08f, back - 0.08f)));
        var holder = at.GetChild<Node3D>(0);
        var main = GarmentMaterial(Garments.Get("backpack"), color, q);
        var dark = GarmentMaterial(Garments.Get("backpack"), color.Darkened(0.55f), q);
        void Box(Vector3 size, Vector3 pos, Material m, float rx = 0)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = pos, Rotation = new Vector3(rx, 0, 0) };
            holder.AddChild(mi);
            Track(mi, false);
        }
        Box(new Vector3(0.27f, 0.4f, 0.15f), Vector3.Zero, main);                               // corps du sac
        Box(new Vector3(0.28f, 0.1f, 0.165f), new Vector3(0, 0.19f, -0.005f), dark, 0.1f);      // rabat
        Box(new Vector3(0.2f, 0.16f, 0.05f), new Vector3(0, -0.08f, -0.095f), dark);            // poche avant
        Box(new Vector3(0.04f, 0.36f, 0.02f), new Vector3(-0.09f, 0.0f, 0.08f), dark);          // bretelles (côté dos)
        Box(new Vector3(0.04f, 0.36f, 0.02f), new Vector3(0.09f, 0.0f, 0.08f), dark);
    }

    void AddBrim(Color color, QualityProfile q)
    {
        var head = _skel.GetBoneGlobalRest(_boneHead).Origin;
        var t = new Transform3D(new Basis(Vector3.Right, 0.12f).Scaled(new Vector3(1, 1, 1.05f)), head + new Vector3(0, 0.125f, 0.115f));
        var at = Attach(_boneHead, t);
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.095f, BottomRadius = 0.095f, Height = 0.012f, RadialSegments = 24 }, MaterialOverride = GarmentMaterial(Garments.Get("cap"), color, q) };
        at.GetChild<Node3D>(0).AddChild(mi);
        Track(mi, true);
    }

    // ---------------------------------------------------------------- matériaux (cache partagé)

    static readonly Dictionary<string, Material> _mats = new();
    static NoiseTexture2D _fabricNormal;

    static Material SkinMaterial(StandardMaterial3D src, Color tint, QualityProfile q)
    {
        string key = $"skin|{src?.ResourceName}|{tint.ToHtml()}|{q.Level}";
        if (_mats.TryGetValue(key, out var m)) return m;
        var s = (StandardMaterial3D)(src?.Duplicate() ?? new StandardMaterial3D());
        s.AlbedoColor = new Color(tint.R, tint.G, tint.B);
        s.NormalEnabled = q.Level >= QualityLevel.Medium && s.NormalTexture != null;
        if (q.Level >= QualityLevel.Ultra)
        {
            // peau : léger transfert de lumière sous la surface (oreilles, doigts au soleil)
            s.SubsurfScatterEnabled = true;
            s.SubsurfScatterStrength = 0.25f;
            s.SubsurfScatterSkinMode = true;
        }
        if (q.Level >= QualityLevel.High) { s.RimEnabled = true; s.Rim = 0.25f; s.RimTint = 0.6f; }
        _mats[key] = s;
        return s;
    }

    static Material HairMaterial(StandardMaterial3D src, Color color, QualityProfile q)
    {
        string key = $"hair|{src?.ResourceName}|{color.ToHtml()}|{q.Level}";
        if (_mats.TryGetValue(key, out var m)) return m;
        var s = (StandardMaterial3D)(src?.Duplicate() ?? new StandardMaterial3D());
        s.AlbedoColor = new Color(color.R, color.G, color.B);
        s.NormalEnabled = q.Level >= QualityLevel.Medium && s.NormalTexture != null;
        if (q.Level >= QualityLevel.High)
        {
            s.Roughness = 0.55f;
            s.AnisotropyEnabled = q.Level >= QualityLevel.Ultra; // reflets étirés le long des mèches
            s.Anisotropy = 0.6f;
        }
        _mats[key] = s;
        return s;
    }

    /// <summary>Tissu : couleur, rugosité, relief du tissage (Moyen et plus), reflets satinés (Élevé et plus).</summary>
    public static Material GarmentMaterial(GarmentDef def, Color color, QualityProfile q)
    {
        string key = $"cloth|{def?.Id}|{color.ToHtml()}|{q.Level}";
        if (_mats.TryGetValue(key, out var m)) return m;
        var s = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = def?.Roughness ?? 0.8f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // intérieur des manches et du col visible
            VertexColorUseAsAlbedo = true,                    // bandes des poignets, du col, fermeture éclair
        };
        if (def != null && q.Level >= QualityLevel.Medium && def.FabricStrength > 0)
        {
            _fabricNormal ??= new NoiseTexture2D
            {
                Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 6,
                Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 0.08f },
            };
            s.NormalEnabled = true;
            s.NormalTexture = _fabricNormal;
            s.NormalScale = def.FabricStrength;
            s.Uv1Scale = new Vector3(def.FabricScale / 10f, def.FabricScale / 10f, 1);
        }
        if (def != null && q.Level >= QualityLevel.High && def.Sheen > 0) { s.RimEnabled = true; s.Rim = def.Sheen; s.RimTint = 0.3f; }
        _mats[key] = s;
        return s;
    }

    // ---------------------------------------------------------------- affichage

    /// <summary>Vue à la première personne : la tête, les cheveux et le chapeau ne projettent plus que leur ombre
    /// (le joueur voit son corps, ses bras et ses jambes en baissant les yeux).</summary>
    public bool FirstPerson
    {
        get => _firstPerson;
        set { _firstPerson = value; ApplyFirstPerson(); }
    }

    void ApplyFirstPerson()
    {
        var shadow = GraphicsQuality.ShadowFor(ShadowCaster.Characters);
        foreach (var g in _headParts)
            g.CastShadow = _firstPerson ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : shadow;
    }

    void ApplyShadows()
    {
        var shadow = GraphicsQuality.ShadowFor(ShadowCaster.Characters);
        foreach (var g in _allParts) g.CastShadow = shadow;
    }

    /// <summary>Position des yeux (repère de ce nœud), selon l'animation en cours : caméra à la première personne.</summary>
    public Vector3 EyeLocal
    {
        get
        {
            if (_skel == null) return new Vector3(0, 1.7f, 0);
            var p = _skel.GetBoneGlobalPose(_boneHead) * _eyeInHead;
            return _visual.Transform * p;
        }
    }

    /// <summary>Position du bassin (repère de ce nœud).</summary>
    public Vector3 PelvisLocal => _skel == null ? new Vector3(0, 0.95f, 0) : _visual.Transform * _skel.GetBoneGlobalPose(_bonePelvis).Origin;

    // ---------------------------------------------------------------- animation

    /// <summary>Joue un geste une fois (« Interact » = ouvrir une porte, « PickUp_Table », « Yes », « Dance »…).</summary>
    public void PlayAction(string anim)
    {
        if (_player == null || !_player.HasAnimation(anim)) return;
        _action = anim;
        Play(anim, 0.15f);
    }

    void Play(string anim, float blend, float speed = 1)
    {
        if (_player == null) return;
        if (_anim != anim)
        {
            if (!_player.HasAnimation(anim)) return;
            _player.Play(anim, blend);
            _anim = anim;
        }
        _player.SpeedScale = speed;
    }

    /// <summary>Choisit l'animation selon le mouvement, l'avance de <paramref name="dt"/>, puis ajoute les mouvements
    /// calculés (regard, pédalage, escalade).</summary>
    public void Update(in CharacterMotion m, double dt)
    {
        if (_player == null) return;
        _sinceUpdate += dt;
        if (UpdateInterval > 0 && _sinceUpdate < UpdateInterval) return;
        dt = _sinceUpdate;
        _sinceUpdate = 0;

        _seat = m.Seat;
        _airTime = m.Grounded || m.Swimming || m.Climbing || m.Seat != CharacterSeat.None ? 0 : _airTime + dt;
        if (_landTimer > 0) _landTimer -= dt;

        // Un geste en cours n'est interrompu que par un vrai déplacement
        if (_action != null)
        {
            bool moving = m.Speed > 0.5 || !m.Grounded || m.Seat != CharacterSeat.None || m.Swimming;
            if (moving || !_player.IsPlaying() || _player.CurrentAnimation != _action) _action = null;
        }

        if (_action == null)
        {
            float s = (float)m.Speed;
            if (m.Seat == CharacterSeat.Passenger) Play("Sitting_Idle", 0.3f);
            else if (m.Seat != CharacterSeat.None) Play("Driving", 0.3f);
            else if (m.Swimming) { if (s > 0.3f) Play("Swim_Fwd", 0.3f, Math.Clamp(s / SwimAnimSpeed, 0.6f, 2f)); else Play("Swim_Idle", 0.3f); }
            else if (m.Climbing) Play("Idle", 0.2f);
            else if (!m.Grounded && _airTime > 0.12) Play(m.VerticalSpeed > 0.5 && _airTime < 0.35 ? "Jump_Start" : "Jump", 0.15f);
            else if (_landTimer > 0) { }
            else if (m.Crouching) { if (s > 0.2f) Play("Crouch_Fwd", 0.2f, Math.Clamp(s / CrouchAnimSpeed, 0.5f, 2f)); else Play("Crouch_Idle", 0.25f); }
            else if (s < 0.15f) Play(m.Idle != null && _player.HasAnimation(m.Idle) ? m.Idle : "Idle", 0.3f);
            else if (s < 2.4f) Play("Walk", 0.2f, Math.Clamp(s / WalkAnimSpeed, 0.5f, 1.8f));
            else if (s < 6.0f) Play("Jog_Fwd", 0.2f, Math.Clamp(s / JogAnimSpeed, 0.7f, 1.6f));
            else Play("Sprint", 0.2f, Math.Clamp(s / SprintAnimSpeed, 0.8f, 1.6f));
        }
        // Réception après une vraie chute
        if (m.Grounded && _anim == "Jump" && m.Speed < 3 && _action == null) { Play("Jump_Land", 0.08f); _landTimer = 0.35; }

        // Os modifiés par les mouvements calculés : remis au repos avant l'animation (certains, comme spine_01,
        // n'ont pas de piste dans les animations et garderaient sinon la rotation de l'image précédente)
        foreach (int b in new[] { _boneNeck, _boneHead, _bonePelvis, _boneSpine1, _boneThighL, _boneThighR, _boneCalfL, _boneCalfR, _boneUpperL, _boneUpperR, _boneLowerL, _boneLowerR })
            if (b >= 0) _skel.SetBonePoseRotation(b, _skel.GetBoneRest(b).Basis.GetRotationQuaternion());
        _player.Advance(dt);
        Procedural(m);
    }

    /// <summary>Mouvements calculés par-dessus l'animation.</summary>
    void Procedural(in CharacterMotion m)
    {
        // Regard : la tête et le cou suivent la caméra (haut / bas)
        float pitch = (float)Math.Clamp(m.LookPitch, -1.0, 0.9);
        if (m.Swimming) pitch = 0;
        // (dans le squelette, le personnage regarde vers +Z : une rotation positive autour de X penche vers l'avant)
        RotateGlobal(_boneNeck, Vector3.Right, -pitch * 0.35f);
        RotateGlobal(_boneHead, Vector3.Right, -pitch * 0.5f);

        if (m.Seat == CharacterSeat.Bike)
        {
            // Pédalage : cuisses et mollets suivent les pédales, buste penché vers le guidon
            float ph = (float)m.Pedal;
            RotateGlobal(_boneSpine1, Vector3.Right, 0.3f);
            RotateGlobal(_boneUpperL, Vector3.Right, 0.15f);
            RotateGlobal(_boneUpperR, Vector3.Right, 0.15f);
            RotateGlobal(_boneThighL, Vector3.Right, 0.25f + 0.4f * Mathf.Sin(ph));
            RotateGlobal(_boneThighR, Vector3.Right, 0.25f + 0.4f * Mathf.Sin(ph + Mathf.Pi));
            RotateGlobal(_boneCalfL, Vector3.Right, 0.15f + 0.45f * Mathf.Cos(ph));
            RotateGlobal(_boneCalfR, Vector3.Right, 0.15f + 0.45f * Mathf.Cos(ph + Mathf.Pi));
            RotateGlobal(_bonePelvis, Vector3.Forward, (float)m.Lean * 0.3f);
        }
        else if (m.Climbing)
        {
            // Escalade : les bras montent au-dessus de la tête et les jambes poussent, en alternance
            float t = (float)(Time.GetTicksMsec() / 1000.0 * 5.0);
            float l = Mathf.Sin(t), r = Mathf.Sin(t + Mathf.Pi);
            RotateGlobal(_boneUpperL, Vector3.Right, -2.25f - 0.35f * l);
            RotateGlobal(_boneUpperR, Vector3.Right, -2.25f - 0.35f * r);
            RotateGlobal(_boneLowerL, Vector3.Right, -0.2f - 0.4f * Math.Max(0, -l));
            RotateGlobal(_boneLowerR, Vector3.Right, -0.2f - 0.4f * Math.Max(0, -r));
            RotateGlobal(_boneThighL, Vector3.Right, -0.6f * Math.Max(0, l));
            RotateGlobal(_boneThighR, Vector3.Right, -0.6f * Math.Max(0, r));
            RotateGlobal(_boneCalfL, Vector3.Right, 0.9f * Math.Max(0, l));
            RotateGlobal(_boneCalfR, Vector3.Right, 0.9f * Math.Max(0, r));
        }
    }

    /// <summary>Tourne un os autour d'un axe du squelette (après l'animation).</summary>
    void RotateGlobal(int bone, Vector3 axis, float angle)
    {
        if (bone < 0 || Mathf.Abs(angle) < 1e-4f) return;
        int parent = _skel.GetBoneParent(bone);
        var pb = parent >= 0 ? _skel.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        var local = _skel.GetBonePoseRotation(bone);
        var rot = new Quaternion(axis.Normalized(), angle);
        var pq = pb.GetRotationQuaternion();
        _skel.SetBonePoseRotation(bone, (pq.Inverse() * rot * pq * local).Normalized());
    }

    /// <summary>Nom de l'animation en cours (tests, HUD de débogage).</summary>
    public string CurrentAnimation => _anim;
}
