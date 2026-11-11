using System;
using Godot;

namespace SteamEarth.Population;

/// <summary>
/// Apparence d'un habitant : sexe, âge (taille des enfants, cheveux gris), origine (teinte de peau plausible),
/// tenue selon la saison et le métier, couleurs au hasard. Toujours la même pour la même personne.
/// La corpulence est arrondie à quelques valeurs pour partager les maillages générés (<see cref="CharacterMeshes"/>).
/// </summary>
public static class Looks
{
    public static CharacterAppearance For(Person p, WeatherSample w)
    {
        var r = new Random(p.Seed);
        var a = new CharacterAppearance { Name = p.First, Body = p.Male ? BodyType.Male : BodyType.Female, AutoOutfit = false };
        var origin = p.Origin ?? p.Nationality;
        int s0 = origin?.SkinMin ?? 0, s1 = origin?.SkinMax ?? 2;
        a.Skin = CharacterAppearance.SkinTones[Math.Clamp(s0 + r.Next(s1 - s0 + 1), 0, CharacterAppearance.SkinTones.Length - 1)].Color;

        // cheveux : plus foncés pour les origines méridionales et lointaines, gris avec l'âge
        int hc = origin == null ? new[] { 1, 2, 2, 3, 3, 1, 4 }[r.Next(7)] : origin.SkinMin >= 1 ? new[] { 0, 0, 1, 1, 2 }[r.Next(5)] : new[] { 1, 2, 3 }[r.Next(3)];
        double gray = p.Age < 40 ? 0 : Math.Clamp((p.Age - 40) / 30.0, 0, 1);
        if (r.NextDouble() < gray) hc = 5;
        a.HairColor = CharacterAppearance.HairColors[hc].Color;
        a.Hair = p.Male
            ? (p.Age > 55 && r.NextDouble() < 0.35 ? "none" : new[] { "short", "short", "buzzed", "buzzed" }[r.Next(4)])
            : p.Age > 60 ? new[] { "buzzed_f", "buns", "buzzed_f" }[r.Next(3)] : new[] { "long", "long", "buns", "buzzed_f" }[r.Next(4)];
        a.Beard = p.Male && p.Age >= 18 && r.NextDouble() < 0.3;

        // taille : enfants plus petits (le modèle fait ~1,80 m / 1,75 m à 1)
        float h = p.Age switch
        {
            < 18 => 0.55f + Math.Clamp(p.Age - 4, 0, 13) * 0.032f,
            _ => 0.94f + (float)r.NextDouble() * 0.1f - (p.Age > 75 ? 0.03f : 0),
        };
        a.Height = h;
        float[] builds = { 0.84f, 0.88f, 0.94f, 1.0f };
        int bi = r.Next(builds.Length);
        if (p.Age < 16) bi = 0; else if (p.Age > 50 && r.NextDouble() < 0.4) bi = Math.Min(3, bi + 1);
        a.Build = builds[bi];

        // tenue
        bool cold = w.TemperatureC < 6, hot = w.TemperatureC > 23;
        string outfit;
        if (cold) outfit = r.NextDouble() < 0.6 ? "winter" : "city";
        else if (hot) outfit = r.NextDouble() < 0.7 ? "summer" : "casual";
        else outfit = new[] { "city", "city", "casual", "casual", "hike" }[r.Next(5)];
        if (p.Job?.Sector == "chantier" && p.School == null && !cold && r.NextDouble() < 0.5) outfit = "worker";
        if (p.Age >= 65 && outfit == "summer" && r.NextDouble() < 0.5) outfit = "city";
        a.Outfit = outfit;
        foreach (var piece in Garments.GetOutfit(outfit).Pieces)
        {
            var def = Garments.Get(piece.Id);
            if (def == null || r.Next(3) == 0) continue; // garde parfois la couleur d'origine
            float hue = (float)r.NextDouble();
            float sat = p.Age >= 60 ? 0.1f + (float)r.NextDouble() * 0.3f : 0.15f + (float)r.NextDouble() * 0.55f;
            float val = 0.2f + (float)r.NextDouble() * 0.65f;
            a.Colors[def.Slot] = Color.FromHsv(hue, sat, val);
        }
        return a;
    }
}
