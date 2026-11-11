using System.Collections.Generic;
using System.Text;
using Godot;

namespace SteamEarth.Population;

/// <summary>Fiche d'un habitant en français ou en anglais (langue de l'interface).</summary>
public static class Story
{
    public static bool English => TranslationServer.GetLocale().StartsWith("en");

    static string L(string fr, string en) => English ? en : fr;
    static string G(Person p, string m, string f) => p.Male ? m : f;

    static string LangName(Lang l) => l switch
    {
        Lang.Fr => L("français", "French"),
        Lang.It => L("italien", "Italian"),
        Lang.Rm => L("romanche", "Romansh"),
        _ => L("allemand", "German"),
    };

    static string Place(Commune c) => c == null ? "?" : $"{c.Name} ({PopulationData.CantonCodes[c.Canton]})";

    static string CountryName(Country c) => English ? c.En : c.Fr;

    /// <summary>Lien de <paramref name="b"/> pour <paramref name="a"/> (« sa fille », « son colocataire »…).</summary>
    public static string Relation(Person a, Person b)
    {
        bool parentA = a.Role is Role.Head or Role.Partner, parentB = b.Role is Role.Head or Role.Partner;
        if (parentA && parentB) return a.Household.Members.Exists(x => x.Role == Role.Partner) ? G(b, L("conjoint", "partner"), L("conjointe", "partner")) : L("colocataire", "flatmate");
        if (b.Role == Role.Flatmate || a.Role == Role.Flatmate) return L("colocataire", "flatmate");
        if (parentA && b.Role == Role.Child) return G(b, L("fils", "son"), L("fille", "daughter"));
        if (a.Role == Role.Child && parentB) return G(b, L("père", "father"), L("mère", "mother"));
        if (a.Role == Role.Child && b.Role == Role.Child) return G(b, L("frère", "brother"), L("sœur", "sister"));
        if (a.Role == Role.Child && b.Role == Role.Grandparent) return G(b, L("grand-père", "grandfather"), L("grand-mère", "grandmother"));
        if (a.Role == Role.Head && b.Role == Role.Grandparent) return G(b, L("père", "father"), L("mère", "mother"));
        if (a.Role == Role.Partner && b.Role == Role.Grandparent) return G(b, L("beau-père", "father-in-law"), L("belle-mère", "mother-in-law"));
        if (a.Role == Role.Grandparent && b.Role == Role.Head) return G(b, L("fils", "son"), L("fille", "daughter"));
        if (a.Role == Role.Grandparent && b.Role == Role.Partner) return G(b, L("gendre", "son-in-law"), L("belle-fille", "daughter-in-law"));
        if (a.Role == Role.Grandparent && b.Role == Role.Child) return G(b, L("petit-fils", "grandson"), L("petite-fille", "granddaughter"));
        return L("proche", "relative");
    }

    public static string AgeText(int age) => age < 1 ? L("moins d'un an", "under a year") : English ? $"{age} {(age == 1 ? "year" : "years")} old" : $"{age} an{(age > 1 ? "s" : "")}";
    public static string AgeShort(int age) => English ? $"{age}" : $"{age} an{(age > 1 ? "s" : "")}";

    /// <summary>Activité : « infirmière à Thun (80 %) », « élève à l'école primaire (3e année) », « retraité »…</summary>
    public static string Activity(Person p)
    {
        string s = p.School ?? "";
        if (s == "creche") return L("va à la crèche", "goes to nursery");
        if (s == "home") return L("reste à la maison avec ses parents", "stays at home with the family");
        if (s == "kindergarten") return L("à l'école enfantine", "at kindergarten");
        if (s == "primary") return English ? $"at primary school (year {p.Age - 5})" : $"élève à l'école primaire ({p.Age - 5}e année)";
        if (s == "secondary") return p.Lang == Lang.Fr ? L("élève au cycle d'orientation", "at secondary school") : p.Lang == Lang.It ? L("élève à la scuola media", "at secondary school") : L("élève à l'école secondaire", "at secondary school");
        if (s == "gymnasium") return p.Lang == Lang.Fr ? L("au gymnase (maturité)", "at upper secondary school (Matura)") : p.Lang == Lang.It ? L("au liceo (maturité)", "at upper secondary school (Matura)") : L("au Gymnasium (maturité)", "at upper secondary school (Matura)");
        if (s == "culture") return L("à l'école de culture générale", "at a specialised secondary school");
        if (s.StartsWith("apprentice:"))
        {
            int i = int.Parse(s.Substring(11));
            string where = p.WorkPlace != null && p.WorkPlace != p.Home ? (English ? $" in {p.WorkPlace.Name}" : $" à {p.WorkPlace.Name}") : "";
            return (English ? "apprentice " : "apprenti" + G(p, "", "e") + " ") + Residents.ApprenticeshipText(i, English) + where + L(" (CFC)", " (VET)");
        }
        if (s.StartsWith("uni:")) return L($"étudiant{G(p, "", "e")} à ", "student at ") + Residents.UniversityText(int.Parse(s.Substring(4)));
        if (s == "jobseeker") return L("cherche du travail", "looking for work");
        if (s == "family") return L($"au foyer, s'occupe de la famille", "stays at home with the family");
        if (s == "retired")
            return p.Job == null ? L(G(p, "retraité", "retraitée"), "retired")
                : English ? $"retired (former {p.Job.En})" : $"{G(p, "retraité", "retraitée")}, ancien{G(p, "", "ne")} {G(p, p.Job.FrM, p.Job.FrF)}";
        if (p.Job != null)
        {
            string job = English ? p.Job.En : G(p, p.Job.FrM, p.Job.FrF);
            string where = p.WorkPlace == null ? "" : English ? $" in {p.WorkPlace.Name}" : $" à {p.WorkPlace.Name}";
            return job + where + (p.WorkRate < 100 ? $" ({p.WorkRate} %)" : "");
        }
        return "";
    }

    /// <summary>Texte complet de la fiche (titre à part).</summary>
    public static string Card(Person p, out string title)
    {
        title = $"{p.FullName}, {AgeText(p.Age)}";
        var sb = new StringBuilder();
        // nationalité, origine
        if (p.Nationality == null)
        {
            sb.Append(L("Suisse", "Swiss"));
            if (p.Heimatort != null && p.Origin == null) sb.Append(L($", originaire de {Place(p.Heimatort)}", $", citizen of {Place(p.Heimatort)}"));
            if (p.Origin != null) sb.Append(p.BornInSwitzerland ? L($", famille venue de : {CountryName(p.Origin)}", $", family from {CountryName(p.Origin)}")
                                                                 : L($", {G(p, "naturalisé", "naturalisée")} (pays de naissance : {CountryName(p.Origin)})", $", naturalised (born in {CountryName(p.Origin)})"));
        }
        else
        {
            sb.Append(L("Nationalité : ", "Nationality: ")).Append(CountryName(p.Nationality));
            sb.Append(p.BornInSwitzerland ? L($", {G(p, "né", "née")} en Suisse", ", born in Switzerland") : L($", en Suisse depuis {p.ArrivalYear}", $", in Switzerland since {p.ArrivalYear}"));
        }
        sb.Append('\n');
        sb.Append(L("Habite à ", "Lives in ")).Append(Place(p.Home));
        var others = p.Household.Members.FindAll(x => x != p);
        if (others.Count == 0) sb.Append(L(G(p, ", seul", ", seule"), ", alone"));
        else
        {
            sb.Append(L(", avec ", ", with "));
            var parts = new List<string>();
            foreach (var o in others) parts.Add($"{o.First} ({Relation(p, o)}, {AgeShort(o.Age)})");
            sb.Append(Join(parts));
        }
        sb.Append('\n');
        string act = Activity(p);
        if (act.Length > 0) sb.Append(char.ToUpper(act[0])).Append(act.Substring(1)).Append('\n');
        sb.Append(L("Parle ", "Speaks ")).Append(LangName(p.Lang));
        if (p.Age >= 6)
            sb.Append(L(" · aime ", " · likes ")).Append(English ? Residents.Hobbies[p.Hobby1].En : Residents.Hobbies[p.Hobby1].Fr)
              .Append(L(" et ", " and ")).Append(English ? Residents.Hobbies[p.Hobby2].En : Residents.Hobbies[p.Hobby2].Fr);
        return sb.ToString();
    }

    static string Join(List<string> parts)
    {
        if (parts.Count == 1) return parts[0];
        return string.Join(", ", parts.GetRange(0, parts.Count - 1)) + L(" et ", " and ") + parts[^1];
    }

    /// <summary>Petite phrase quand on salue quelqu'un (dans sa langue).</summary>
    public static string Greeting(Person p, int hour)
    {
        bool evening = hour >= 18 || hour < 5;
        return p.Lang switch
        {
            Lang.Fr => evening ? "Bonsoir !" : "Bonjour !",
            Lang.It => evening ? "Buonasera!" : "Buongiorno!",
            Lang.Rm => "Allegra!",
            _ => hour < 11 && hour >= 5 ? "Guete Morge!" : evening ? "Guete Abig!" : "Grüezi!",
        };
    }
}
