using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>
/// Apparence d'un personnage : corps, peau, coiffure, tenue. Sert au joueur (choisie dans le menu Personnage,
/// gardée dans user://settings.cfg) et servira aux passants (apparences tirées au hasard, <see cref="Random"/>).
/// </summary>
public sealed class CharacterAppearance
{
    public string Name = "Alex";
    public BodyType Body = BodyType.Male;
    /// <summary>Teinte de la peau (multiplie la texture ; 1,1,1 = teinte d'origine).</summary>
    public Color Skin = SkinTones[2].Color;
    public string Hair = "short";
    public Color HairColor = HairColors[1].Color;
    public bool Beard;
    /// <summary>Taille (1 = 1,80 m environ pour l'homme, 1,75 m pour la femme).</summary>
    public float Height = 1f;
    /// <summary>Corpulence : 0,85 = mince, 0,86 = normale, 1 = athlétique (corps d'origine).</summary>
    public float Build = 0.86f;
    /// <summary>Tenue de base (identifiant de <see cref="Garments.Outfits"/>).</summary>
    public string Outfit = "hike";
    /// <summary>Couleurs choisies par le joueur, par emplacement (sinon celles de la tenue).</summary>
    public Dictionary<GarmentSlot, Color> Colors = new();
    /// <summary>La tenue change selon l'activité (vélo, avion, nage, neige…).</summary>
    public bool AutoOutfit = true;

    public static readonly (string Name, Color Color)[] SkinTones =
    {
        ("Très claire", new Color(1.22f, 1.12f, 1.08f)),
        ("Claire", new Color(1.12f, 1.04f, 0.98f)),
        ("Moyenne", new Color(1f, 1f, 1f)),
        ("Mate", new Color(0.82f, 0.74f, 0.66f)),
        ("Foncée", new Color(0.6f, 0.48f, 0.4f)),
        ("Très foncée", new Color(0.4f, 0.31f, 0.26f)),
    };

    public static readonly (string Name, Color Color)[] HairColors =
    {
        ("Noir", new Color(0.18f, 0.16f, 0.15f)),
        ("Brun", new Color(0.55f, 0.4f, 0.3f)),
        ("Châtain", new Color(0.85f, 0.62f, 0.42f)),
        ("Blond", new Color(1.5f, 1.25f, 0.85f)),
        ("Roux", new Color(1.25f, 0.62f, 0.35f)),
        ("Gris", new Color(1.1f, 1.1f, 1.1f)),
    };

    /// <summary>Personnages proposés au départ (le joueur peut ensuite tout modifier).</summary>
    public static CharacterAppearance[] Presets() => new[]
    {
        new CharacterAppearance { Name = "Alex", Body = BodyType.Male, Skin = SkinTones[2].Color, Hair = "short", HairColor = HairColors[1].Color, Beard = true, Outfit = "hike" },
        new CharacterAppearance { Name = "Léa", Body = BodyType.Female, Skin = SkinTones[1].Color, Hair = "long", HairColor = HairColors[2].Color, Outfit = "hike" },
        new CharacterAppearance { Name = "Malik", Body = BodyType.Male, Skin = SkinTones[4].Color, Hair = "buzzed", HairColor = HairColors[0].Color, Outfit = "city" },
        new CharacterAppearance { Name = "Yuna", Body = BodyType.Female, Skin = SkinTones[3].Color, Hair = "buns", HairColor = HairColors[0].Color, Outfit = "casual" },
        new CharacterAppearance { Name = "Sven", Body = BodyType.Male, Skin = SkinTones[0].Color, Hair = "short", HairColor = HairColors[3].Color, Outfit = "winter" },
        new CharacterAppearance { Name = "Amara", Body = BodyType.Female, Skin = SkinTones[5].Color, Hair = "buzzed_f", HairColor = HairColors[0].Color, Outfit = "summer" },
    };

    /// <summary>Vêtements portés pour une activité donnée (tenue de base, ou tenue de l'activité si la tenue auto est active).</summary>
    public Garment[] GarmentsFor(GameMode mode)
    {
        var outfit = AutoOutfit ? Garments.ForMode(mode, Outfit) : Garments.GetOutfit(Outfit);
        bool own = outfit.Id == Outfit; // couleurs personnalisées seulement sur la tenue choisie
        return outfit.Pieces.Select(p =>
        {
            var def = Garments.Get(p.Id);
            return own && def != null && Colors.TryGetValue(def.Slot, out var c) ? new Garment(p.Id, c) : p;
        }).ToArray();
    }

    public CharacterAppearance Clone()
    {
        var c = (CharacterAppearance)MemberwiseClone();
        c.Colors = new Dictionary<GarmentSlot, Color>(Colors);
        return c;
    }

    /// <summary>Apparence au hasard (passants) ; même graine = même personne.</summary>
    public static CharacterAppearance Random(int seed)
    {
        var r = new System.Random(seed);
        var a = new CharacterAppearance { Body = r.Next(2) == 0 ? BodyType.Male : BodyType.Female };
        a.Skin = SkinTones[r.Next(SkinTones.Length)].Color;
        a.HairColor = HairColors[r.Next(HairColors.Length)].Color;
        a.Hair = a.Body == BodyType.Male ? new[] { "short", "buzzed", "short", "none" }[r.Next(4)] : new[] { "long", "buns", "buzzed_f", "long" }[r.Next(4)];
        a.Beard = a.Body == BodyType.Male && r.Next(3) == 0;
        a.Height = 0.93f + (float)r.NextDouble() * 0.12f;
        a.Build = 0.84f + (float)r.NextDouble() * 0.16f;
        a.Outfit = new[] { "city", "casual", "summer", "hike", "worker" }[r.Next(5)];
        a.AutoOutfit = false;
        foreach (var p in Garments.GetOutfit(a.Outfit).Pieces)
        {
            var def = Garments.Get(p.Id);
            if (def == null || r.Next(2) == 0) continue;
            a.Colors[def.Slot] = Color.FromHsv((float)r.NextDouble(), 0.2f + (float)r.NextDouble() * 0.5f, 0.25f + (float)r.NextDouble() * 0.6f);
        }
        return a;
    }

    // ---------------------------------------------------------------- sauvegarde (user://settings.cfg)

    public string Serialize()
    {
        var inv = CultureInfo.InvariantCulture;
        string Col(Color c) => string.Format(inv, "{0:0.###},{1:0.###},{2:0.###}", c.R, c.G, c.B);
        var parts = new List<string>
        {
            "name=" + Name.Replace(";", " ").Replace("=", " "),
            "body=" + (Body == BodyType.Male ? "m" : "f"),
            "skin=" + Col(Skin), "hair=" + Hair, "haircolor=" + Col(HairColor), "beard=" + (Beard ? 1 : 0),
            "height=" + Height.ToString("0.###", inv), "build=" + Build.ToString("0.###", inv), "outfit=" + Outfit, "auto=" + (AutoOutfit ? 1 : 0),
        };
        foreach (var kv in Colors) parts.Add("c_" + kv.Key + "=" + Col(kv.Value));
        return string.Join(";", parts);
    }

    public static CharacterAppearance Parse(string s)
    {
        var a = new CharacterAppearance();
        if (string.IsNullOrEmpty(s)) return a;
        var inv = CultureInfo.InvariantCulture;
        Color Col(string v)
        {
            var p = v.Split(',');
            return p.Length == 3 ? new Color(float.Parse(p[0], inv), float.Parse(p[1], inv), float.Parse(p[2], inv)) : new Color(1, 1, 1);
        }
        foreach (var part in s.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq], v = part[(eq + 1)..];
            try
            {
                switch (k)
                {
                    case "name": a.Name = v; break;
                    case "body": a.Body = v == "f" ? BodyType.Female : BodyType.Male; break;
                    case "skin": a.Skin = Col(v); break;
                    case "hair": a.Hair = v; break;
                    case "haircolor": a.HairColor = Col(v); break;
                    case "beard": a.Beard = v == "1"; break;
                    case "height": a.Height = Math.Clamp(float.Parse(v, inv), 0.85f, 1.15f); break;
                    case "build": a.Build = Math.Clamp(float.Parse(v, inv), 0.8f, 1.05f); break;
                    case "outfit": a.Outfit = v; break;
                    case "auto": a.AutoOutfit = v == "1"; break;
                    default:
                        if (k.StartsWith("c_") && Enum.TryParse<GarmentSlot>(k[2..], out var slot)) a.Colors[slot] = Col(v);
                        break;
                }
            }
            catch (FormatException) { }
        }
        return a;
    }
}

/// <summary>Personnage du joueur : apparence choisie (menu Personnage) et activité en cours.</summary>
public static class PlayerAppearance
{
    const string SettingsPath = "user://settings.cfg";
    static CharacterAppearance _current;
    static GameMode _mode = GameMode.Explore;

    /// <summary>Appelé quand l'apparence ou l'activité change : les modèles du joueur (à pied, dans les véhicules) se reconstruisent.</summary>
    public static event Action Changed;

    public static CharacterAppearance Current
    {
        get
        {
            if (_current == null)
            {
                var cfg = new ConfigFile();
                cfg.Load(SettingsPath);
                string s = (string)cfg.GetValue("personnage", "apparence", "");
                _current = string.IsNullOrEmpty(s) ? CharacterAppearance.Presets()[0] : CharacterAppearance.Parse(s);
            }
            return _current;
        }
    }

    public static GameMode Mode => _mode;

    public static void Set(CharacterAppearance a, bool save = true)
    {
        _current = a.Clone();
        if (save)
        {
            var cfg = new ConfigFile();
            cfg.Load(SettingsPath);
            cfg.SetValue("personnage", "apparence", _current.Serialize());
            cfg.Save(SettingsPath);
        }
        Changed?.Invoke();
    }

    /// <summary>Change l'activité (vélo, avion, nage…) : avec la tenue auto, les vêtements suivent.</summary>
    public static void SetMode(GameMode m)
    {
        if (m == _mode) return;
        var before = Current.GarmentsFor(_mode);
        _mode = m;
        if (!before.SequenceEqual(Current.GarmentsFor(m))) Changed?.Invoke();
    }
}
