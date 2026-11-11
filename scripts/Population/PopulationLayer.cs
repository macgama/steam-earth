using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth.Population;

/// <summary>
/// Passants de la Suisse, d'après le vrai nombre d'habitants de chaque hectare (STATPOP, OFS).
///
/// Autour du joueur (à pied, en voiture, à vélo… près du sol), on parcourt les hectares habités à moins de
/// <see cref="QualityProfile.PedestrianRadius"/> m. Chaque habitant a une chance d'être dehors qui dépend de l'heure
/// locale (peu de monde la nuit, du monde aux heures de pointe et à midi), du jour, de la météo réelle et de son âge
/// (élèves en classe le matin, aînés surtout de jour). Le tirage est fixé par quart d'heure : la même personne reste
/// dehors un moment, puis rentre. Les enfants de moins de 12 ans sortent avec un parent, les couples souvent
/// ensemble. Au plus <see cref="QualityProfile.PedestrianMax"/> passants à la fois (les plus proches).
///
/// Touche I : la fiche du passant le plus proche (nom, âge, origine, ménage, métier).
/// </summary>
public partial class PopulationLayer : Node3D
{
    public FlyCamera Camera;
    public Atmosphere Atmosphere;

    sealed class Outing
    {
        public Person Leader;
        public readonly List<Person> Followers = new();
        public double Distance;
    }

    readonly Dictionary<long, List<Pedestrian>> _active = new(); // clé : guide de la sortie
    readonly Queue<Outing> _toSpawn = new();
    readonly HashSet<long> _queued = new();
    double _timer;
    int _wanted;
    bool _enabled;
    PersonCard _card;
    Pedestrian _talking;

    public int Count { get; private set; }
    /// <summary>Plus personne à faire apparaître (captures automatiques).</summary>
    public bool Idle => _toSpawn.Count == 0 && _timer < 0.55;
    public int Wanted => _wanted;
    public string Status => !PopulationData.Loaded ? "données absentes" : !_enabled ? "hors de Suisse ou en vol" : $"{Count} passants (voulus {_wanted}, trottoirs {Walkways.Count:N0})";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Pausable;
        PopulationData.Init();
        _card = new PersonCard { Name = "PersonCard" };
        AddChild(_card);
        GraphicsQuality.Changed += OnQuality;
    }

    public override void _ExitTree() => GraphicsQuality.Changed -= OnQuality;

    void OnQuality(QualityProfile now, QualityProfile previous)
    {
        if (now.PedestrianMax < previous.PedestrianMax) _timer = 1; // retire les passants en trop tout de suite
    }

    // ---------------------------------------------------------------- qui est dehors ?

    /// <summary>Part des habitants dehors près de chez eux à cette heure (0..1).</summary>
    public static double OutdoorRate(double hour, DayOfWeek day, Person p, WeatherSample w)
    {
        // fraction d'habitants dans la rue, heure par heure (pointes à 8 h, midi et 17-18 h)
        double[] byHour = { 0.003, 0.0015, 0.001, 0.001, 0.0015, 0.004, 0.012, 0.026, 0.028, 0.022, 0.024, 0.030,
                            0.036, 0.030, 0.026, 0.027, 0.033, 0.040, 0.036, 0.027, 0.019, 0.013, 0.008, 0.005 };
        int h0 = ((int)Math.Floor(hour) % 24 + 24) % 24;
        double f = hour - Math.Floor(hour);
        double rate = byHour[h0] * (1 - f) + byHour[(h0 + 1) % 24] * f;
        bool weekend = day is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (weekend) rate *= hour < 9 ? 0.4 : hour > 10 && hour < 18 ? 1.25 : 1;
        bool school = !weekend && (hour >= 8 && hour < 11.8 || hour >= 13.6 && hour < 15.8);
        int a = p.Age;
        if (a < 20 && p.School != null && school) rate *= 0.12;
        if (a < 16 && hour >= 21) rate *= 0.15;
        if (a >= 70) rate *= hour >= 8.5 && hour < 17 ? 1.4 : hour >= 20 || hour < 7 ? 0.25 : 0.8;
        if (a >= 85) rate *= 0.5;
        if (w.PrecipMmH > 3) rate *= 0.25; else if (w.PrecipMmH > 0.3) rate *= 0.45;
        if (w.TemperatureC < -5) rate *= 0.7;
        if (w.Wind10Speed > 15) rate *= 0.6;
        return rate;
    }

    /// <summary>Sorties voulues autour de (lat, lon), les plus proches d'abord.</summary>
    List<Outing> Desired(double lat, double lon, Vec3d player, double radius, int max)
    {
        var res = new List<Outing>();
        PopulationData.CellAt(lat, lon, out int e0, out int n0);
        Data.SwissAlti3D.ToLV95(lat, lon, out double pe, out double pn);
        var utc = Atmosphere.GameUtc;
        var local = TimeZones.LocalTime(utc, lat, lon, out _, out _, out _);
        double hour = local.TimeOfDay.TotalHours;
        long slot = (long)Math.Floor((utc - new DateTime(2020, 1, 1)).TotalMinutes / 15);
        var w = Atmosphere.Current;
        int reach = (int)Math.Ceiling(radius / 100);
        var taken = new HashSet<Person>();
        for (int de = -reach; de <= reach; de++)
            for (int dn = -reach; dn <= reach; dn++)
            {
                int e = e0 + de, n = n0 + dn;
                double cd = Math.Sqrt(Math.Pow(e * 100 + 50 - pe, 2) + Math.Pow(n * 100 + 50 - pn, 2));
                if (cd > radius + 50) continue;
                var people = Residents.Of(e, n);
                if (people.Count == 0) continue;
                Household last = null;
                Outing family = null;
                foreach (var p in people)
                {
                    if (p.Household != last) { last = p.Household; family = null; }
                    if (p.Age < 12 || taken.Contains(p)) continue;
                    double rate = OutdoorRate(hour, local.DayOfWeek, p, w);
                    if (Residents.Hash(p.Seed, (int)slot, 1) / (double)int.MaxValue >= rate) continue;
                    bool adult = p.Role is Role.Head or Role.Partner or Role.Grandparent && p.Age >= 18;
                    // le conjoint sort souvent avec l'autre
                    if (family != null && adult && Residents.Hash(p.Seed, (int)slot, 2) % 10 < 6)
                    {
                        family.Followers.Add(p); taken.Add(p);
                        continue;
                    }
                    var o = new Outing { Leader = p, Distance = cd + (p.Index % 7) };
                    taken.Add(p);
                    if (adult)
                    {
                        family ??= o;
                        // les enfants de moins de 12 ans viennent avec un parent (une fois sur deux)
                        if (Residents.Hash(p.Seed, (int)slot, 3) % 2 == 0)
                            foreach (var k in p.Household.Members)
                                if (k.Role == Role.Child && k.Age >= 4 && k.Age < 12 && !taken.Contains(k) && !(k.School != null && OutdoorRate(hour, local.DayOfWeek, k, w) < 0.002))
                                { o.Followers.Add(k); taken.Add(k); }
                    }
                    res.Add(o);
                }
            }
        res.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        // au plus « max » personnes en tout
        int count = 0, keep = 0;
        foreach (var o in res)
        {
            int c = 1 + o.Followers.Count;
            if (count + c > max) break;
            count += c; keep++;
        }
        _wanted = 0;
        foreach (var o in res) _wanted += 1 + o.Followers.Count;
        return res.GetRange(0, keep);
    }

    // ---------------------------------------------------------------- chaque image

    public override void _Process(double delta)
    {
        if (Camera == null || !PopulationData.Loaded) return;
        var player = Camera.PlayerPos;
        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);
        double ground = HeightProvider.SurfaceHeight(lat, lon);
        double above = player.Length() - Geo.Radius - ground;
        bool near = above < 120 && Camera.Speed < 70;
        PopulationData.CellAt(lat, lon, out int pe, out int pn);
        bool swiss = Data.SwissAlti3D.Covers(lat, lon) && lat > 45.7 && lat < 47.9 && lon > 5.9 && lon < 10.6;
        _enabled = near && swiss;
        var q = GraphicsQuality.Current;

        _timer -= delta;
        if (_timer <= 0)
        {
            _timer = 0.6;
            var wanted = _enabled ? Desired(lat, lon, player, q.PedestrianRadius, q.PedestrianMax) : new List<Outing>();
            var keys = new HashSet<long>();
            foreach (var o in wanted) keys.Add(o.Leader.Key);
            // retire ceux qui ne sont plus dehors (s'ils sont loin ou hors de la vue), et ceux trop loin
            var remove = new List<long>();
            foreach (var (k, list) in _active)
            {
                var lead = list[0];
                double d = (lead.PosD - player).Length();
                bool gone = d > q.PedestrianRadius + 60 || !_enabled;
                if (!keys.Contains(k) && (d > 60 || !InView(lead.PosD)) && lead != _talking) gone = true;
                if (gone) remove.Add(k);
            }
            foreach (var k in remove) Despawn(k);
            // nouveaux : pas sous les yeux du joueur, pas tous dans la même image
            int room = q.PedestrianMax - Count;
            foreach (var o in wanted)
            {
                if (_active.ContainsKey(o.Leader.Key) || _queued.Contains(o.Leader.Key)) continue;
                int c = 1 + o.Followers.Count;
                if (c > room) continue;
                room -= c;
                _toSpawn.Enqueue(o); _queued.Add(o.Leader.Key);
            }
        }

        // au plus deux personnages construits par image (chaque modèle prend quelques millisecondes)
        for (int i = 0; i < 2 && _toSpawn.Count > 0; i++)
        {
            var o = _toSpawn.Dequeue();
            _queued.Remove(o.Leader.Key);
            if (!_enabled || Count + 1 + o.Followers.Count > q.PedestrianMax) continue;
            SpawnOuting(o, player);
        }

        // déplacement et affichage
        var cam = Camera.PosD;
        var rot = Camera.WorldRotation;
        foreach (var list in _active.Values)
            foreach (var p in list)
            {
                if (p.Leader == null) p.Step(delta, player);
                else p.Step(delta, player);
                double d = (p.PosD - cam).Length();
                p.Model.Visible = d < q.PedestrianRadius + 50;
                if (p.Model.Visible) p.Place(cam, rot, delta, d, q.PedestrianSmoothDistance);
            }

        UpdateCard(player, lat, lon);
    }

    bool InView(Vec3d p)
    {
        var to = p - Camera.PosD;
        double d = to.Length();
        if (d < 1e-3) return true;
        var up = Camera.PosD.Normalized();
        Geo.LocalFrame(up, out var e, out var n);
        var fwd = n * Math.Cos(Camera.Yaw) + e * Math.Sin(Camera.Yaw);
        return to.Dot(fwd) / d > 0.35;
    }

    void SpawnOuting(Outing o, Vec3d player)
    {
        var lead = Make(o.Leader);
        if (!lead.Spawn(o.Leader.CellE, o.Leader.CellN)) { lead.Model.QueueFree(); return; }
        // pas d'apparition sous les yeux du joueur, tout près
        if ((lead.PosD - player).Length() < 35 && InView(lead.PosD)) { lead.Model.QueueFree(); return; }
        var list = new List<Pedestrian> { lead };
        int rank = 0;
        foreach (var f in o.Followers)
        {
            var p = Make(f);
            p.Leader = lead; p.FollowerRank = rank++;
            p.PosD = lead.PosD; p.Yaw = lead.Yaw;
            list.Add(p);
        }
        _active[o.Leader.Key] = list;
        Count += list.Count;
    }

    Pedestrian Make(Person person)
    {
        var model = new CharacterModel { Name = "Passant_" + person.First };
        AddChild(model);
        var a = Looks.For(person, Atmosphere.Current);
        model.SetAppearance(a, GameMode.City);
        model.Visible = false;
        return new Pedestrian(person, model);
    }

    void Despawn(long key)
    {
        if (!_active.Remove(key, out var list)) return;
        foreach (var p in list) p.Model.QueueFree();
        Count -= list.Count;
        if (_talking != null && list.Contains(_talking)) CloseCard();
    }

    // ---------------------------------------------------------------- fiche (touche I)

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is not InputEventKey k || !k.Pressed || k.Echo || k.PhysicalKeycode != Key.I || Camera == null || Camera.InputBlocked) return;
        if (_talking != null) { CloseCard(); GetViewport().SetInputAsHandled(); return; }
        var p = Nearest(Camera.PlayerPos, 8);
        if (p == null) { _card.Hint(Loc.T("Aucun passant à côté de toi")); return; }
        Talk(p);
        GetViewport().SetInputAsHandled();
    }

    Pedestrian Nearest(Vec3d pos, double max)
    {
        Pedestrian best = null; double bd = max;
        foreach (var list in _active.Values)
            foreach (var p in list)
            {
                if (!p.Model.Visible) continue;
                double d = (p.PosD - pos).Length();
                if (d < bd) { bd = d; best = p; }
            }
        return best;
    }

    void Talk(Pedestrian p)
    {
        CloseCard();
        var lead = p.Leader ?? p;
        _talking = p;
        lead.Talking = true;
        lead.LookAt = Camera.PlayerPos;
        var local = TimeZones.LocalTime(Atmosphere.GameUtc, 46.8, 8.2, out _, out _, out _);
        string text = Story.Card(p.Person, out string title);
        _card.Open(title, Story.Greeting(p.Person, local.Hour), text);
    }

    void CloseCard()
    {
        if (_talking != null) { var lead = _talking.Leader ?? _talking; lead.Talking = false; lead.LookAt = null; }
        _talking = null;
        _card.Close();
    }

    void UpdateCard(Vec3d player, double lat, double lon)
    {
        if (_talking != null)
        {
            var lead = _talking.Leader ?? _talking;
            lead.LookAt = player;
            if ((_talking.PosD - player).Length() > 12 || !_enabled) CloseCard();
            return;
        }
        // petit rappel quand on passe tout près de quelqu'un (à pied)
        var p = Camera.Walk ? Nearest(player, 2.5) : null;
        _card.Near(p == null ? null : $"{ControlsPanel.KeyText("{I}")} : {Loc.T("parler à")} {p.Person.First}");
    }

    /// <summary>Tests : nombre de passants et fiche du plus proche.</summary>
    public string Describe()
    {
        var p = Nearest(Camera.PlayerPos, 1e9);
        if (p == null) return Status;
        string text = Story.Card(p.Person, out string title);
        return $"{Status}\n{title} — {(p.PosD - Camera.PlayerPos).Length():F0} m\n{text}";
    }

    /// <summary>Tests : ouvre la fiche du passant le plus proche.</summary>
    public bool TalkToNearest()
    {
        var p = Nearest(Camera.PlayerPos, 1e9);
        if (p == null) return false;
        Talk(p);
        return true;
    }

    /// <summary>Tests : le passant le plus proche (pour placer la caméra devant lui).</summary>
    public Pedestrian NearestPedestrian() => Nearest(Camera.PlayerPos, 1e9);
}
