using System;
using System.Globalization;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>
/// Studio de test des personnages (scène scenes/CharacterPreview.tscn), sans charger la Terre :
/// plusieurs personnages côte à côte sur un sol neutre, capture d'écran puis sortie.
/// Arguments (après « -- ») :
///   --set=presets|outfits|bodies   qui afficher (personnages prêts, toutes les tenues, homme/femme)
///   --anim=Walk                    animation (ou « bike », « climb », « swim »)
///   --time=0.4                     secondes d'animation avant la capture
///   --cam=front|side|back|close    point de vue
///   --quality=0..3                 niveau graphique
///   --shot=fichier.png             capture puis quitte
/// </summary>
public partial class CharacterPreview : Node3D
{
    CharacterModel[] _models;
    CharacterMotion _motion;
    double _time = 0.6, _elapsed;
    string _shot;
    int _frames;
    string _anim = "Idle";

    public override void _Ready()
    {
        var inv = CultureInfo.InvariantCulture;
        string set = "presets", cam = "front";
        int[] only = null;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--set=")) set = a[6..];
            if (a.StartsWith("--anim=")) _anim = a[7..];
            if (a.StartsWith("--time=")) _time = double.Parse(a[7..], inv);
            if (a.StartsWith("--cam=")) cam = a[6..];
            if (a.StartsWith("--shot=")) _shot = a[7..];
            if (a.StartsWith("--only=")) only = a[7..].Split(',').Select(int.Parse).ToArray();
        }
        GraphicsQuality.Init();

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.62f, 0.72f, 0.82f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.7f, 0.75f, 0.85f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            SsaoEnabled = GraphicsQuality.Current.Ssao,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { LightEnergy = 1.5f, ShadowEnabled = true, Rotation = new Vector3(-0.75f, 0.6f, 0) };
        AddChild(sun);
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(40, 40) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.55f, 0.35f), Roughness = 1 },
        };
        AddChild(ground);

        CharacterAppearance[] people = set switch
        {
            "outfits" => Garments.Outfits.Select((o, i) =>
            {
                var p = CharacterAppearance.Presets()[i % 2];
                p.Outfit = o.Id; p.AutoOutfit = false;
                return p;
            }).ToArray(),
            "bodies" => CharacterAppearance.Presets().Take(2).ToArray(),
            "one" => CharacterAppearance.Presets().Take(1).ToArray(),
            _ => CharacterAppearance.Presets(),
        };
        if (only != null) people = only.Where(i => i < people.Length).Select(i => people[i]).ToArray();
        float spacing = 0.95f;
        _models = new CharacterModel[people.Length];
        for (int i = 0; i < people.Length; i++)
        {
            var m = new CharacterModel { Position = new Vector3((i - (people.Length - 1) / 2f) * spacing, 0, 0) };
            AddChild(m);
            m.SetAppearance(people[i], GameMode.Explore);
            _models[i] = m;
        }

        _motion = new CharacterMotion { Grounded = true };
        switch (_anim)
        {
            case "Walk": _motion.Speed = 1.4; break;
            case "Jog": _motion.Speed = 4; break;
            case "Sprint": _motion.Speed = 9; break;
            case "swim": _motion.Swimming = true; _motion.Speed = 1.2; break;
            case "climb": _motion.Climbing = true; _motion.Grounded = false; break;
            case "bike": _motion.Seat = CharacterSeat.Bike; break;
            case "drive": _motion.Seat = CharacterSeat.Car; break;
            case "sit": _motion.Seat = CharacterSeat.Passenger; break;
            case "crouch": _motion.Crouching = true; break;
            case "jump": _motion.Grounded = false; _motion.VerticalSpeed = -2; break;
            case "Idle": break;
            default: foreach (var m in _models) m.PlayAction(_anim); break;
        }

        float width = people.Length * spacing;
        var camera = new Camera3D { Fov = 40, Current = true };
        AddChild(camera);
        float dist = Math.Max(3.2f, width * 1.5f);
        Vector3 pos = cam switch
        {
            "side" => new Vector3(dist, 1.1f, 0.01f),
            "back" => new Vector3(0, 1.3f, dist),
            "close" => new Vector3(0, 1.6f, -1.6f),
            "hips" => new Vector3(0.3f, 0.95f, -1.5f),
            "feet" => new Vector3(0.4f, 0.4f, -1.4f),
            "top" => new Vector3(0.01f, dist, -0.5f),
            _ => new Vector3(0, 1.3f, -dist),
        };
        camera.Position = pos;
        camera.LookAt(new Vector3(0, cam switch { "close" => 1.55f, "hips" => 0.95f, "feet" => 0.15f, _ => 0.9f }, 0));
    }

    public override void _Process(double delta)
    {
        double dt = 1.0 / 30;
        if (_elapsed < _time)
        {
            if (_motion.Seat == CharacterSeat.Bike) _motion.Pedal += dt * 6;
            foreach (var m in _models) m.Update(_motion, dt);
            _elapsed += dt;
            return;
        }
        if (_shot == null) { foreach (var m in _models) m.Update(_motion, delta); return; }
        if (++_frames < 4) return;
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_shot);
        GD.Print($"[Studio] capture : {_shot} (animation {_models[0].CurrentAnimation}, yeux {_models[0].EyeLocal}, bassin {_models[0].PelvisLocal})");
        GetTree().Quit();
    }
}
