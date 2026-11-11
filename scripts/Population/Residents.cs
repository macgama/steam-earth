using System;
using System.Collections.Generic;

namespace SteamEarth.Population;

/// <summary>Place dans le ménage (pour décrire les liens entre ses membres).</summary>
public enum Role { Head, Partner, Child, Grandparent, Flatmate }

/// <summary>Un habitant (fictif) d'un hectare : toujours le même pour le même hectare et le même rang.</summary>
public sealed class Person
{
    public int CellE, CellN, Index;
    public Household Household;
    public Role Role;
    public bool Male;
    public int Age, BirthYear;
    public string First, Last;
    /// <summary>Nationalité étrangère (null = Suisse).</summary>
    public Country Nationality;
    /// <summary>Pays d'origine de la famille (Suisses naturalisés, deuxième génération) ; null = famille suisse.</summary>
    public Country Origin;
    public bool BornInSwitzerland = true;
    public int ArrivalYear;
    public Lang Lang;
    public Commune Home;
    /// <summary>Lieu d'origine (Suisses : commune inscrite sur le passeport).</summary>
    public Commune Heimatort;
    public Occupation Job;
    public Commune WorkPlace;
    public int WorkRate = 100;
    public string School;
    public int Hobby1, Hobby2;
    public int Seed;
    public long Key => ((long)CellE * 20000 + CellN) * 4096 + Index;
    public string FullName => First + " " + Last;
}

public sealed class Household
{
    public readonly List<Person> Members = new();
    public string Surname;
}

/// <summary>Métier : intitulés français (masculin, féminin) et anglais, poids (part des actifs), secteur.</summary>
public sealed record Occupation(string FrM, string FrF, string En, double Weight, string Sector = "");

/// <summary>
/// Fabrique les habitants d'un hectare à partir de ses chiffres STATPOP : nombre d'habitants, puis ménages
/// (tailles du carré de 1 km), âges et sexes (pyramide du carré), nationalités, langue de la commune.
/// Déterministe : la même graine (coordonnées de l'hectare) redonne toujours les mêmes personnes.
/// </summary>
public static class Residents
{
    public static int CurrentYear = DateTime.UtcNow.Year;

    static readonly Dictionary<int, List<Person>> Cache = new();
    static readonly LinkedList<int> Lru = new();
    const int CacheMax = 400;

    public static int Hash(int a, int b, int c = 0)
    {
        unchecked
        {
            uint h = 2166136261;
            h = (h ^ (uint)a) * 16777619; h ^= h >> 13;
            h = (h ^ (uint)b) * 16777619; h ^= h >> 15;
            h = (h ^ (uint)c) * 16777619; h ^= h >> 16;
            h *= 0x85ebca6b; h ^= h >> 13;
            return (int)(h & 0x7fffffff);
        }
    }

    /// <summary>Habitants de l'hectare (e, n) (vide si inhabité). Résultat mis en cache.</summary>
    public static List<Person> Of(int e, int n)
    {
        int key = e * 20000 + n;
        if (Cache.TryGetValue(key, out var list)) return list;
        list = Generate(e, n);
        Cache[key] = list;
        Lru.AddLast(key);
        while (Lru.Count > CacheMax) { Cache.Remove(Lru.First.Value); Lru.RemoveFirst(); }
        return list;
    }

    static List<Person> Generate(int e, int n)
    {
        var people = new List<Person>();
        int pop = PopulationData.Residents(e, n, out var commune);
        if (pop <= 0 || commune == null) return people;
        var prof = PopulationData.Profile(e, n);
        var r = new Random(Hash(e, n, 77));
        while (people.Count < pop)
        {
            int size = 1 + Sample(r, prof.Households);
            if (size == 6) size += r.Next(3); // « 6 personnes et plus »
            size = Math.Min(size, pop - people.Count);
            MakeHousehold(r, prof, commune, size, people, e, n);
        }
        for (int i = 0; i < people.Count; i++) { people[i].Index = i; people[i].CellE = e; people[i].CellN = n; people[i].Seed = Hash(e, n, i + 1000); }
        return people;
    }

    // ---------------------------------------------------------------- tirages

    static int Sample(Random r, float[] w, int from = 0, int to = -1)
    {
        if (to < 0) to = w.Length;
        double t = 0;
        for (int i = from; i < to; i++) t += w[i];
        if (t <= 0) return from + r.Next(to - from);
        double x = r.NextDouble() * t;
        for (int i = from; i < to; i++) { x -= w[i]; if (x <= 0) return i; }
        return to - 1;
    }

    /// <summary>Âge (et sexe si <paramref name="male"/> est null) tiré de la pyramide, entre min et max ans.</summary>
    static int SampleAge(Random r, BlockProfile p, int min, int max, ref bool? male)
    {
        double total = 0;
        Span<double> w = stackalloc double[38];
        for (int s = 0; s < 2; s++)
        {
            if (male is bool m && (m ? 0 : 1) != s) continue;
            for (int k = 0; k < 19; k++)
            {
                int a0 = k * 5, a1 = k == 18 ? 100 : k * 5 + 4;
                int lo = Math.Max(a0, min), hi = Math.Min(a1, max);
                if (hi < lo) continue;
                double frac = (hi - lo + 1) / (double)(a1 - a0 + 1);
                w[s * 19 + k] = Math.Max(p.AgeSex[s * 19 + k], 0.3) * frac; // jamais tout à fait nul (petits hectares)
                total += w[s * 19 + k];
            }
        }
        if (total <= 0) { male ??= r.Next(2) == 0; return Math.Clamp(min + r.Next(Math.Max(1, max - min + 1)), min, max); }
        double x = r.NextDouble() * total;
        int idx = 0;
        for (int i = 0; i < 38; i++) { x -= w[i]; if (x <= 0) { idx = i; break; } idx = i; }
        male ??= idx < 19;
        int c = idx % 19;
        int b0 = Math.Max(c * 5, min), b1 = Math.Min(c == 18 ? 100 : c * 5 + 4, max);
        if (c == 18) b1 = Math.Min(b1, 90 + (int)(Math.Abs(r.NextDouble() - r.NextDouble()) * 12));
        return b0 + r.Next(Math.Max(1, b1 - b0 + 1));
    }

    static Person NewPerson(Household h, Role role, bool male, int age, Commune home)
    {
        var p = new Person { Household = h, Role = role, Male = male, Age = age, BirthYear = CurrentYear - age, Home = home };
        h.Members.Add(p);
        return p;
    }

    static void MakeHousehold(Random r, BlockProfile prof, Commune home, int size, List<Person> people, int e, int n)
    {
        var h = new Household();
        bool? m = null;
        if (size == 1)
        {
            int a = SampleAge(r, prof, 19, 100, ref m);
            NewPerson(h, Role.Head, m.Value, a, home);
        }
        else
        {
            double kind = r.NextDouble();
            if (size == 2 && kind > 0.86)
            {
                // colocation (jeunes adultes)
                for (int i = 0; i < 2; i++) { m = null; int a = SampleAge(r, prof, 19, 34, ref m); NewPerson(h, i == 0 ? Role.Head : Role.Flatmate, m.Value, a, home); }
            }
            else
            {
                bool single = size >= 2 && kind < (size == 2 ? 0.16 : 0.12); // famille monoparentale
                int maxParent = size == 2 && !single ? 100 : 62;
                int a = SampleAge(r, prof, size == 2 && !single ? 21 : 24, maxParent, ref m);
                var head = NewPerson(h, Role.Head, m.Value, a, home);
                if (!single)
                {
                    bool same = r.NextDouble() < 0.03;
                    int pa = Math.Clamp(a + (head.Male ? -1 : 1) * (int)Math.Round(Gauss(r) * 4 + 2), 19, 100);
                    NewPerson(h, Role.Partner, same ? head.Male : !head.Male, pa, home);
                }
                int kids = size - h.Members.Count;
                if (kids > 0 && size >= 4 && r.NextDouble() < 0.08) { kids--; m = null; NewPerson(h, Role.Grandparent, r.Next(2) == 0, Math.Min(100, a + 25 + r.Next(10)), home); }
                int youngest = Math.Max(0, a - 45), oldest = Math.Max(0, Math.Min(25, a - 18));
                for (int i = 0; i < kids; i++)
                {
                    m = null;
                    int ka = oldest >= youngest ? SampleAge(r, prof, youngest, oldest, ref m) : 0;
                    NewPerson(h, Role.Child, m.Value, ka, home);
                }
            }
        }
        Dress(r, prof, h, home);
        people.AddRange(h.Members);
    }

    static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>Nationalité, langue, noms, métier et loisirs de tous les membres du ménage.</summary>
    static void Dress(Random r, BlockProfile prof, Household h, Commune home)
    {
        Lang region = r.NextDouble() < home.Lang1Share ? home.Lang1 : home.Lang2;
        int group = Sample(r, prof.Nationality);
        Country country = group == 0 ? null : Names.PickCountry(r, group, home.Lang1);
        // Suisses dont la famille vient d'ailleurs (naturalisés, deuxième génération)
        Country origin = country;
        if (country == null && r.NextDouble() < 0.16) origin = Names.PickCountry(r, Sample(r, new[] { 0, prof.Nationality[1], prof.Nationality[2], prof.Nationality[3] }, 1, 4), home.Lang1);
        var head = h.Members[0];
        int oldestAge = 0;
        foreach (var p in h.Members) oldestAge = Math.Max(oldestAge, p.Role is Role.Head or Role.Partner ? p.Age : 0);
        int arrival = origin == null ? 0 : CurrentYear - (int)(r.NextDouble() * Math.Min(45, Math.Max(1, oldestAge - 16))) - 1;
        h.Surname = origin == null ? Names.SwissSurname(r, region) : Names.Surname(r, origin);
        var heimatort = PickHeimatort(r, home);

        foreach (var p in h.Members)
        {
            p.Lang = region;
            p.Origin = origin;
            bool flatmate = p.Role == Role.Flatmate;
            // conjoint d'une autre origine, colocataire indépendant
            if (flatmate || p.Role == Role.Partner && r.NextDouble() < 0.12)
            {
                Country other = r.NextDouble() < 0.7 ? null : Names.PickCountry(r, 1 + Sample(r, new[] { prof.Nationality[1], prof.Nationality[2], prof.Nationality[3] }), home.Lang1);
                p.Origin = other;
                p.Nationality = other;
                p.Last = other == null ? Names.SwissSurname(r, region) : Names.Surname(r, other);
                p.First = other == null ? Names.SwissFirst(r, region, p.Male, p.BirthYear) : Names.First(r, other, p.Male, p.BirthYear);
                p.BornInSwitzerland = other == null;
                p.ArrivalYear = other == null ? 0 : CurrentYear - 1 - r.Next(Math.Max(1, Math.Min(30, p.Age - 17)));
                p.Heimatort = other == null ? PickHeimatort(r, home) : null;
            }
            else
            {
                p.Nationality = country;
                bool bornHere = origin == null || p.BirthYear > arrival;
                p.BornInSwitzerland = bornHere;
                p.ArrivalYear = bornHere ? 0 : arrival;
                // les enfants nés ici d'une famille étrangère sont parfois déjà suisses
                if (country != null && bornHere && p.Role == Role.Child && r.NextDouble() < 0.25) p.Nationality = null;
                p.Last = p.Role == Role.Partner && r.NextDouble() < 0.3
                    ? (origin == null ? Names.SwissSurname(r, region) : Names.Surname(r, origin)) : h.Surname;
                p.First = origin == null ? Names.SwissFirst(r, region, p.Male, p.BirthYear)
                    : bornHere ? Names.ChildFirst(r, origin, region, p.Male, p.BirthYear) : Names.First(r, origin, p.Male, p.BirthYear);
                p.Heimatort = p.Nationality == null ? (origin == null ? heimatort : home) : null;
            }
            Occupy(r, p, home);
            p.Hobby1 = r.Next(Hobbies.Length);
            p.Hobby2 = (p.Hobby1 + 1 + r.Next(Hobbies.Length - 1)) % Hobbies.Length;
        }
        // deux membres ne portent pas le même prénom
        for (int i = 1; i < h.Members.Count; i++)
            for (int j = 0; j < i; j++)
                if (h.Members[i].First == h.Members[j].First)
                    h.Members[i].First = h.Members[i].Origin == null ? Names.SwissFirst(r, region, h.Members[i].Male, h.Members[i].BirthYear + 7) : Names.First(r, h.Members[i].Origin, h.Members[i].Male, 0);
    }

    static Commune PickHeimatort(Random r, Commune home)
    {
        if (r.NextDouble() < 0.3) return home;
        var l = PopulationData.InCanton(r.NextDouble() < 0.7 ? home.Canton : 1 + r.Next(26));
        return l.Count > 0 ? l[r.Next(l.Count)] : home;
    }

    // ---------------------------------------------------------------- activité

    public static readonly Occupation[] Jobs =
    {
        new("vendeur", "vendeuse", "shop assistant", 6, "commerce"),
        new("employé de commerce", "employée de commerce", "office clerk", 9, "bureau"),
        new("infirmier", "infirmière", "nurse", 5, "santé"),
        new("assistant en soins", "assistante en soins", "care assistant", 3, "santé"),
        new("médecin", "médecin", "doctor", 1.5, "santé"),
        new("pharmacien", "pharmacienne", "pharmacist", 0.6, "santé"),
        new("enseignant", "enseignante", "teacher", 4.5, "école"),
        new("éducateur de la petite enfance", "éducatrice de la petite enfance", "early childhood educator", 1.5, "école"),
        new("informaticien", "informaticienne", "software developer", 3.5, "bureau"),
        new("ingénieur", "ingénieure", "engineer", 2.5, "bureau"),
        new("comptable", "comptable", "accountant", 2, "bureau"),
        new("conseiller bancaire", "conseillère bancaire", "bank adviser", 2, "banque"),
        new("gestionnaire d'assurances", "gestionnaire d'assurances", "insurance specialist", 1.5, "banque"),
        new("électricien", "électricienne", "electrician", 2, "chantier"),
        new("maçon", "maçonne", "bricklayer", 1.5, "chantier"),
        new("menuisier", "menuisière", "carpenter", 1.5, "chantier"),
        new("installateur sanitaire", "installatrice sanitaire", "plumber", 1.2, "chantier"),
        new("peintre en bâtiment", "peintre en bâtiment", "painter and decorator", 1, "chantier"),
        new("polymécanicien", "polymécanicienne", "machinist", 2, "industrie"),
        new("horloger", "horlogère", "watchmaker", 0.8, "industrie"),
        new("laborantin", "laborantine", "lab technician", 1, "industrie"),
        new("logisticien", "logisticienne", "logistics worker", 2, "industrie"),
        new("mécanicien automobile", "mécanicienne automobile", "car mechanic", 1.2, "industrie"),
        new("cuisinier", "cuisinière", "cook", 2, "hôtellerie"),
        new("serveur", "serveuse", "waiter", 2, "hôtellerie"),
        new("réceptionniste d'hôtel", "réceptionniste d'hôtel", "hotel receptionist", 0.8, "hôtellerie"),
        new("boulanger-pâtissier", "boulangère-pâtissière", "baker", 0.8, "commerce"),
        new("coiffeur", "coiffeuse", "hairdresser", 0.8, "commerce"),
        new("chauffeur de bus", "chauffeuse de bus", "bus driver", 1, "transport"),
        new("conducteur de train", "conductrice de train", "train driver", 0.5, "transport"),
        new("chauffeur poids lourd", "chauffeuse poids lourd", "truck driver", 1, "transport"),
        new("facteur", "factrice", "postal worker", 0.8, "transport"),
        new("policier", "policière", "police officer", 0.8, "public"),
        new("employé communal", "employée communale", "municipal employee", 1.2, "public"),
        new("assistant social", "assistante sociale", "social worker", 1, "public"),
        new("agriculteur", "agricultrice", "farmer", 2, "ferme"),
        new("fromager", "fromagère", "cheesemaker", 0.3, "ferme"),
        new("forestier-bûcheron", "forestière-bûcheronne", "forester", 0.3, "ferme"),
        new("architecte", "architecte", "architect", 0.8, "bureau"),
        new("avocat", "avocate", "lawyer", 0.6, "bureau"),
        new("graphiste", "graphiste", "graphic designer", 0.6, "bureau"),
        new("journaliste", "journaliste", "journalist", 0.4, "bureau"),
        new("chercheur", "chercheuse", "researcher", 0.8, "bureau"),
        new("agent de nettoyage", "agente de nettoyage", "cleaner", 1.5, "service"),
        new("gardien d'immeuble", "gardienne d'immeuble", "caretaker", 0.6, "service"),
        new("moniteur de ski", "monitrice de ski", "ski instructor", 0.2, "tourisme"),
        new("guide de montagne", "guide de montagne", "mountain guide", 0.1, "tourisme"),
        new("chef d'entreprise", "cheffe d'entreprise", "business owner", 1.2, "bureau"),
    };

    static readonly (string Fr, string En)[] Apprenticeships =
    {
        ("employé·e de commerce", "commercial employee"), ("assistant·e en soins", "healthcare assistant"),
        ("informaticien·ne", "IT technician"), ("électricien·ne de montage", "electrician"), ("polymécanicien·ne", "machinist"),
        ("gestionnaire du commerce de détail", "retail specialist"), ("cuisinier·ère", "cook"), ("menuisier·ère", "carpenter"),
        ("dessinateur·rice", "draughtsman"), ("logisticien·ne", "logistics specialist"), ("mécatronicien·ne automobile", "car mechatronics technician"),
        ("agriculteur·rice", "farmer"), ("coiffeur·euse", "hairdresser"), ("assistant·e socio-éducatif·ve", "social care worker"),
    };

    public static readonly (string Fr, string En)[] Hobbies =
    {
        ("la randonnée", "hiking"), ("le ski", "skiing"), ("le jass", "playing jass"), ("le football", "football"),
        ("la lecture", "reading"), ("la cuisine", "cooking"), ("le vélo", "cycling"), ("la natation", "swimming"),
        ("la musique", "music"), ("la photographie", "photography"), ("le jardinage", "gardening"), ("le cinéma", "films"),
        ("les jeux vidéo", "video games"), ("la course à pied", "running"), ("le hockey sur glace", "ice hockey"),
        ("la chorale", "choir singing"), ("l'escalade", "climbing"), ("la pêche", "fishing"), ("les voyages", "travelling"),
        ("le tennis", "tennis"), ("le yoga", "yoga"), ("les échecs", "chess"), ("la fanfare du village", "the village brass band"),
        ("le bricolage", "DIY"), ("la gym", "the gym"), ("le snowboard", "snowboarding"), ("la lutte suisse", "Swiss wrestling"),
    };

    static readonly (string Name, double E, double N)[] Universities =
    {
        ("l'Université de Zurich", 2683600, 1247800), ("l'EPF de Zurich", 2683700, 1248000), ("l'Université de Berne", 2600200, 1200300),
        ("l'Université de Bâle", 2611200, 1267400), ("l'Université de Lausanne", 2534000, 1152000), ("l'EPFL", 2533200, 1152300),
        ("l'Université de Genève", 2499900, 1117200), ("l'Université de Fribourg", 2578400, 1183900), ("l'Université de Neuchâtel", 2561900, 1205100),
        ("l'Université de la Suisse italienne", 2717700, 1096300), ("l'Université de Saint-Gall", 2745600, 1254500), ("l'Université de Lucerne", 2666600, 1211700),
    };

    static void Occupy(Random r, Person p, Commune home)
    {
        int a = p.Age;
        p.Job = null; p.School = null; p.WorkPlace = null; p.WorkRate = 100;
        if (a < 4) p.School = r.NextDouble() < 0.45 ? "creche" : "home";
        else if (a < 6) p.School = "kindergarten";
        else if (a < 12) p.School = "primary";
        else if (a < 15) p.School = "secondary";
        else if (a < 19)
        {
            double x = r.NextDouble();
            if (a == 15 || x < 0.25) p.School = a == 15 ? "secondary" : "gymnasium";
            else if (x < 0.9) { p.School = "apprentice:" + r.Next(Apprenticeships.Length); p.WorkPlace = WorkCommune(r, home); }
            else p.School = "culture";
        }
        else if (a < 26 && r.NextDouble() < (a < 23 ? 0.38 : 0.2))
        {
            double best = double.MaxValue; int bi = 0;
            for (int i = 0; i < Universities.Length; i++)
            {
                double d = Math.Pow(Universities[i].E - home.E, 2) + Math.Pow(Universities[i].N - home.N, 2);
                d *= 0.5 + r.NextDouble(); // pas toujours la plus proche
                if (d < best) { best = d; bi = i; }
            }
            p.School = "uni:" + bi;
        }
        else if (a >= 65 || a >= 63 && !p.Male && r.NextDouble() < 0.5)
        {
            p.School = "retired";
            p.Job = PickJob(r, home, p.Male);
        }
        else
        {
            double x = r.NextDouble();
            if (x < 0.04) p.School = "jobseeker";
            else if (x < (p.Male ? 0.06 : 0.13)) p.School = "family";
            else
            {
                p.Job = PickJob(r, home, p.Male);
                p.WorkPlace = WorkCommune(r, home);
                double part = p.Male ? 0.18 : 0.58;
                if (r.NextDouble() < part) p.WorkRate = new[] { 40, 50, 60, 70, 80, 80, 90 }[r.Next(7)];
            }
        }
    }

    static Occupation PickJob(Random r, Commune home, bool male)
    {
        bool rural = home.Population < 2500, mountain = home.Canton is 4 or 18 or 23 or 2 && home.Population < 8000;
        double total = 0;
        foreach (var j in Jobs) total += W(j);
        double x = r.NextDouble() * total;
        foreach (var j in Jobs) { x -= W(j); if (x <= 0) return j; }
        return Jobs[0];

        double W(Occupation j) => j.Weight * j.Sector switch
        {
            "ferme" => rural ? 4 : 0.3,
            "tourisme" => mountain ? 12 : 0.2,
            "banque" or "bureau" => rural ? 0.6 : 1.2,
            "chantier" => male ? 1.6 : 0.15,
            "santé" or "école" => male ? 0.5 : 1.5,
            _ => 1,
        };
    }

    static Commune WorkCommune(Random r, Commune home)
    {
        if (r.NextDouble() < 0.42 || home.Population > 60000) return home;
        var big = PopulationData.NearestCommune(home.E + (r.NextDouble() - 0.5) * 30000, home.N + (r.NextDouble() - 0.5) * 30000, 15000, home);
        if (big == null) return home;
        double d = Math.Sqrt(Math.Pow(big.E - home.E, 2) + Math.Pow(big.N - home.N, 2));
        return d < 45000 ? big : home;
    }

    public static string ApprenticeshipText(int i, bool en) => en ? Apprenticeships[i].En : Apprenticeships[i].Fr;
    public static string UniversityText(int i) => Universities[i].Name;
}
