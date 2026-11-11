using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth.Population;

/// <summary>
/// Un passant à l'écran : un habitant (<see cref="Person"/>) qui marche le long des trottoirs et des chemins
/// (<see cref="Walkways"/>), s'arrête parfois (téléphone, bras croisés, discussion), tourne aux carrefours.
/// Sans route connue autour de lui, il flâne autour de chez lui. Un enfant ou un conjoint qui l'accompagne le
/// suit à côté (<see cref="Leader"/>). Position en double dans le repère planète, nœud placé relativement à la caméra.
/// </summary>
public sealed class Pedestrian
{
    public readonly Person Person;
    public readonly CharacterModel Model;
    public Pedestrian Leader;
    public int FollowerRank;
    public Vec3d PosD;
    public double Yaw;
    public double Speed { get; private set; }
    /// <summary>Arrêté pour parler au joueur (fiche ouverte) : se tourne vers lui.</summary>
    public bool Talking;
    public Vec3d? LookAt;

    readonly Random _rng;
    readonly double _cruise;
    Walkway _way;
    double _s;
    int _dir = 1, _side = 1;
    double _pause, _sinceCheck, _stuck;
    string _idle;
    Vec3d _target, _home;
    bool _free;
    int _wayVersion;

    public Pedestrian(Person p, CharacterModel model)
    {
        Person = p; Model = model;
        _rng = new Random(p.Seed ^ 0x5bd1e995);
        int a = p.Age;
        _cruise = a < 12 ? 1.15 + _rng.NextDouble() * 0.3
            : a < 18 ? 1.3 + _rng.NextDouble() * 0.3
            : a < 65 ? 1.25 + _rng.NextDouble() * 0.3
            : a < 80 ? 0.95 + _rng.NextDouble() * 0.25
            : 0.7 + _rng.NextDouble() * 0.2;
        if (a >= 18 && a < 55 && _rng.NextDouble() < 0.04) _cruise = 2.6 + _rng.NextDouble() * 0.6; // joggeur
    }

    public bool Jogger => _cruise > 2;

    /// <summary>Place le passant dans son hectare (sur un trottoir s'il y en a un). Faux s'il n'a pas pu être placé.</summary>
    public bool Spawn(int cellE, int cellN)
    {
        PopulationData.ToWgs84(cellE * 100 + 50, cellN * 100 + 50, out double lat, out double lon);
        _home = Ground(Geo.FromLatLon(lat, lon));
        double dLat = 90 / 111_000.0, dLon = dLat / Math.Cos(lat * Math.PI / 180);
        var ways = Walkways.In(lat - dLat, lat + dLat, lon - dLon, lon + dLon);
        if (ways.Count > 0)
        {
            // un point de trottoir dans l'hectare (ou le plus proche du centre)
            double bestD = double.MaxValue; Walkway bw = null; double bs = 0;
            for (int k = 0; k < 24; k++)
            {
                var w = ways[_rng.Next(ways.Count)];
                double s = _rng.NextDouble() * w.Length;
                double d = (w.At(s, out _) - _home).Length();
                if (d < bestD) { bestD = d; bw = w; bs = s; }
                if (d < 45) break;
            }
            if (bw != null && bestD < 120)
            {
                _way = bw; _s = bs; _dir = _rng.Next(2) == 0 ? 1 : -1;
                _side = bw.Footway ? (_rng.Next(2) == 0 ? 1 : -1) : (_rng.Next(2) == 0 ? 1 : -1);
                _free = false;
                PosD = OnWay(out var dir);
                FaceAlong(dir * _dir);
                _wayVersion = Walkways.Version;
                return true;
            }
        }
        // pas de route connue (pas encore chargée, ou hameau isolé) : flâne autour de chez lui
        _free = true;
        PosD = Ground(Offset(_home, (_rng.NextDouble() - 0.5) * 60, (_rng.NextDouble() - 0.5) * 60));
        NewTarget();
        return !double.IsNaN(PosD.X);
    }

    /// <summary>Suit son guide (enfant, conjoint) : à côté de lui, un peu en retrait.</summary>
    public void Follow(double dt)
    {
        var l = Leader;
        var up = l.PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var fwd = north * Math.Cos(l.Yaw) + east * Math.Sin(l.Yaw);
        var right = fwd.Cross(up).Normalized();
        double side = (FollowerRank % 2 == 0 ? 0.75 : -0.75) * (1 + FollowerRank / 2);
        var want = Ground(l.PosD + right * side - fwd * (0.15 + 0.35 * (FollowerRank / 2)));
        var delta = want - PosD;
        double d = delta.Length();
        // rattrape doucement (pas de téléportation, sauf s'il est très loin)
        if (d > 8) PosD = want;
        else if (d > 0.02) PosD = Ground(PosD + delta * Math.Min(1, dt * 3));
        Speed = l.Speed > 0.1 ? Math.Max(l.Speed, d * 1.5) : d > 0.3 ? d : 0;
        Yaw = l.Talking && l.LookAt is Vec3d t ? YawTo(t) : l.Yaw;
        _idle = l._idle == "Idle_Talking" ? "Idle_Talking" : null;
    }

    public void Step(double dt, Vec3d player)
    {
        if (Leader != null) { Follow(dt); return; }
        if (Talking)
        {
            Speed = 0; _idle = "Idle_Talking";
            if (LookAt is Vec3d t) Yaw = TurnTowards(Yaw, YawTo(t), dt * 4);
            return;
        }
        if (_pause > 0)
        {
            _pause -= dt; Speed = 0;
            return;
        }
        _idle = null;
        // le joueur barre le passage : on s'arrête un instant
        var toPlayer = player - PosD;
        if (toPlayer.Length() < 1.3 && !Jogger)
        {
            var up = PosD.Normalized();
            Geo.LocalFrame(up, out var e, out var n);
            var fwd = n * Math.Cos(Yaw) + e * Math.Sin(Yaw);
            if (toPlayer.Dot(fwd) > 0) { _pause = 1.2; Speed = 0; return; }
        }
        Speed = _cruise;
        if (_free || _way == null || !Walkways.Alive(_way)) { StepFree(dt); return; }

        _s += _dir * Speed * dt;
        if (_s < 0 || _s > _way.Length) Junction();
        var p = OnWay(out var dir);
        var move = p - PosD;
        PosD = p;
        if (move.Length() > 1e-4) Yaw = TurnTowards(Yaw, YawOf(move), dt * 6);
        // pauses : téléphone, attente, discussion
        if (_rng.NextDouble() < dt / (Jogger ? 400 : 45))
        {
            _pause = 3 + _rng.NextDouble() * 9;
            double x = _rng.NextDouble();
            _idle = x < 0.35 ? "Idle_TalkingPhone" : x < 0.55 ? "Idle_FoldArms" : x < 0.7 && Person.Age > 14 ? "Idle_Talking" : "Idle";
        }
        // demi-tour de temps en temps (balade)
        if (_rng.NextDouble() < dt / 240) _dir = -_dir;
    }

    /// <summary>Fin de la ligne : on continue sur une autre qui part de ce carrefour, sinon demi-tour.</summary>
    void Junction()
    {
        bool atEnd = _s > _way.Length;
        var end = atEnd ? _way.Points[^1] : _way.Points[0];
        var next = Walkways.EndsNear(end, 4, _way);
        if (next.Count > 0)
        {
            // pas trop souvent dans la même direction d'où l'on vient
            var (w, atStart) = next[_rng.Next(next.Count)];
            _way = w;
            _dir = atStart ? 1 : -1;
            _s = atStart ? 0.01 : w.Length - 0.01;
            if (!w.Footway && _rng.NextDouble() < 0.5) _side = -_side; // traverse la rue
        }
        else
        {
            _dir = -_dir;
            _s = Math.Clamp(_s, 0, _way.Length);
            if (!_way.Footway) _side = -_side; // traverse et repart sur l'autre trottoir
        }
    }

    Vec3d OnWay(out Vec3d dir)
    {
        var axis = _way.At(_s, out dir);
        var up = axis.Normalized();
        var side = dir.Cross(up).Normalized();
        var p = axis + side * (_side * _way.SideOffset);
        double axisAlt = axis.Length() - Geo.Radius;
        var g = Ground(p);
        // sur un pont : la chaussée est au-dessus du relief
        double ga = g.Length() - Geo.Radius;
        return axisAlt > ga + 1.2 ? p.Normalized() * (Geo.Radius + axisAlt) : g;
    }

    void StepFree(double dt)
    {
        _free = true;
        var to = _target - PosD;
        var up = PosD.Normalized();
        to -= up * to.Dot(up);
        double d = to.Length();
        if (d < 0.6) { _pause = 2 + _rng.NextDouble() * 6; NewTarget(); Speed = 0; return; }
        var before = PosD;
        var next = PosD + to * (Math.Min(d, Speed * dt) / d);
        next = Obstacles.Resolve(next, 0.3, 1.8);
        PosD = Ground(next);
        Yaw = TurnTowards(Yaw, YawOf(to), dt * 5);
        _stuck = (PosD - before).Length() < Speed * dt * 0.3 ? _stuck + dt : 0;
        if (_stuck > 1.5) { _stuck = 0; NewTarget(); }
        // une route est arrivée près de lui : il la rejoint
        _sinceCheck += dt;
        if (_sinceCheck > 3 && Walkways.Version != _wayVersion)
        {
            _sinceCheck = 0; _wayVersion = Walkways.Version;
            Geo.ToLatLon(PosD.Normalized(), out double lat, out double lon);
            double dl = 30 / 111_000.0;
            foreach (var w in Walkways.In(lat - dl, lat + dl, lon - dl * 1.5, lon + dl * 1.5))
            {
                double s = Nearest(w, PosD, out double dist);
                if (dist < 25) { _way = w; _s = s; _free = false; _target = w.At(s, out _); break; }
            }
        }
    }

    void NewTarget() => _target = Ground(Offset(_home, (_rng.NextDouble() - 0.5) * 70, (_rng.NextDouble() - 0.5) * 70));

    static double Nearest(Walkway w, Vec3d p, out double dist)
    {
        dist = double.MaxValue; double best = 0;
        for (int i = 0; i < w.Points.Length; i++)
        {
            double d = (w.Points[i] - p).Length();
            if (d < dist) { dist = d; best = w.Along[i]; }
        }
        return best;
    }

    // ---------------------------------------------------------------- outils

    static Vec3d Offset(Vec3d p, double eastM, double northM)
    {
        var up = p.Normalized();
        Geo.LocalFrame(up, out var e, out var n);
        return p + e * eastM + n * northM;
    }

    /// <summary>Pose le point sur le sol (relief 0,5 m en Suisse, toits plats et escaliers des décors).</summary>
    public static Vec3d Ground(Vec3d p)
    {
        var up = p.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        double h = HeightProvider.SurfaceHeight(lat, lon);
        return up * (Geo.Radius + h);
    }

    double YawOf(Vec3d v)
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var e, out var n);
        return Math.Atan2(v.Dot(e), v.Dot(n));
    }

    double YawTo(Vec3d t) => YawOf(t - PosD);

    void FaceAlong(Vec3d dir) => Yaw = YawOf(dir);

    static double TurnTowards(double from, double to, double k)
    {
        double diff = Math.IEEERemainder(to - from, 2 * Math.PI);
        return from + diff * Math.Min(1, k);
    }

    /// <summary>Animation et position du nœud (relatives à la caméra).</summary>
    public void Place(Vec3d cameraPos, Basis worldRotation, double dt, double distance, double smoothDistance)
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var fwd = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var right = fwd.Cross(up).Normalized();
        var basis = new Basis(right.ToVector3(), up.ToVector3(), (-fwd).ToVector3());
        Model.Transform = new Transform3D(worldRotation * basis, worldRotation * (PosD - cameraPos).ToVector3());
        Model.UpdateInterval = distance < smoothDistance ? 0 : distance < smoothDistance * 2.5 ? 1 / 15.0 : 1 / 6.0;
        Model.Update(new CharacterMotion { Speed = Speed, Grounded = true, Idle = _idle }, dt);
    }
}
