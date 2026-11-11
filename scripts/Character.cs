using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Personnage jouable (mode marche, touche F) : modèle 3D animé (<see cref="CharacterModel"/> : corps, coiffure et
/// tenue choisis dans le menu Personnage) qui marche, court, saute, nage et grimpe sur le relief, avec une gravité
/// dirigée vers le centre de la Terre.
///
/// La position réelle est en double (<see cref="PosD"/>, position des pieds dans le repère planète).
/// Comme tout le reste du monde, le nœud Godot est placé chaque image relativement à la caméra
/// (<see cref="Place"/>), ce qui garde la précision partout sur le globe.
/// </summary>
public partial class Character : Node3D
{
    public Vec3d PosD;           // pieds, repère planète
    public double Yaw;           // cap du corps (radians, 0 = nord, π/2 = est)
    public bool Grounded { get; private set; }
    public double GroundHeight { get; private set; }
    public double Speed { get; private set; } // vitesse horizontale actuelle (m/s)
    public double Altitude => PosD.Length() - Geo.Radius;

    public const double Height = 1.8;
    public const double BodyRadius = 0.3;
    public const double ClimbSpeed = 2.5;  // m/s en escaladant un mur (Espace contre le mur)
    public bool Climbing { get; private set; }
    public const double JumpSpeed = 4.5;   // m/s → saut d'environ 1 m
    public const double Gravity = 9.81;
    /// <summary>Dans l'eau profonde (lac, rivière, mer) : il nage, la tête hors de l'eau.</summary>
    public bool Swimming { get; private set; }
    // origine du modèle sous la surface : nage allongée (crawl/brasse, visage à fleur d'eau)
    // ou sur place (debout, la tête dehors)
    public const double SwimDepth = 1.08, TreadDepth = 1.45;
    public bool Crouching;                 // accroupi (touche C)
    /// <summary>Regard vers le haut / le bas (radians) : la tête suit la caméra.</summary>
    public double LookPitch;

    double _verticalSpeed;
    CharacterModel _model;

    /// <summary>Modèle 3D (tenue, animations) ; les véhicules en créent d'autres pour le conducteur.</summary>
    public CharacterModel Model => _model;

    public override void _Ready()
    {
        // Godot : l'avant du personnage est −Z, le haut +Y, l'origine aux pieds.
        _model = new CharacterModel { Name = "Model", IsPlayer = true };
        AddChild(_model);
    }

    /// <summary>Vue à la première personne : le corps reste visible (bras, jambes), la tête ne projette que son ombre.</summary>
    public bool FirstPerson
    {
        get => _model?.FirstPerson ?? false;
        set { if (_model != null) _model.FirstPerson = value; }
    }

    /// <summary>Position des yeux (repère planète), d'après l'animation en cours.</summary>
    public Vec3d EyePos
    {
        get
        {
            var up = PosD.Normalized();
            Geo.LocalFrame(up, out var east, out var north);
            var fwd = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
            var right = fwd.Cross(up).Normalized();
            var e = _model?.EyeLocal ?? new Vector3(0, 1.7f, 0);
            // repère du nœud : +X = droite, +Y = haut, −Z = avant
            return PosD + right * e.X + up * e.Y - fwd * e.Z;
        }
    }

    /// <summary>Pose le personnage sur le sol en (lat, lon).</summary>
    public void PlaceOnGround(double lat, double lon, double yaw)
    {
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        PosD = Geo.FromLatLon(lat, lon) * (Geo.Radius + GroundHeight);
        Yaw = yaw;
        _verticalSpeed = 0;
        Grounded = true;
    }

    /// <summary>Place le personnage à une position quelconque (il tombera s'il est en l'air).</summary>
    public void PlaceAt(Vec3d feet, double yaw)
    {
        PosD = feet;
        Yaw = yaw;
        _verticalSpeed = 0;
        Grounded = false;
    }

    /// <summary>
    /// Un pas de simulation : <paramref name="move"/> est une direction horizontale (repère planète,
    /// longueur 0 = immobile), <paramref name="speed"/> en m/s.
    /// </summary>
    public void Step(Vec3d move, double speed, bool jump, double dt)
    {
        var up = PosD.Normalized();
        if (Swimming) speed = Math.Min(speed, speed > 5 ? 2.4 : 1.4); // nage : brasse, crawl avec Maj
        else if (Crouching) speed = Math.Min(speed, 1.6);
        Speed = 0;
        bool blocked = false;
        if (move.Length() > 1e-6)
        {
            var dir = move.Normalized();
            // Décors solides (troncs, murs) : on glisse le long au lieu de les traverser.
            // Pas de 25 cm au plus, pour ne jamais sauter par-dessus un tronc quand les images sont lentes.
            double dist = speed * dt;
            int steps = Math.Clamp((int)Math.Ceiling(dist / 0.25), 1, 200);
            for (int k = 0; k < steps; k++)
            {
                PosD += dir * (dist / steps);
                PosD = Obstacles.Resolve(PosD, BodyRadius, Height, out bool b);
                if (b) blocked = true;
                // marches et escaliers : on monte à chaque pas (sinon, quand les images sont lentes, le pas suivant
                // arrive contre une marche trop haute et le personnage est repoussé)
                if (Grounded)
                {
                    double surf = Obstacles.SurfaceBelow(PosD);
                    double a = Altitude;
                    if (surf > a && surf - a <= Obstacles.StepUp) PosD = PosD.Normalized() * (Geo.Radius + surf);
                }
            }
            Speed = speed;
            // Le corps se tourne progressivement vers la direction de marche
            Geo.LocalFrame(up, out var east, out var north);
            double target = Math.Atan2(dir.Dot(east), dir.Dot(north));
            double diff = Math.IEEERemainder(target - Yaw, 2 * Math.PI);
            Yaw += diff * Math.Min(1, dt * 12);
        }

        // Eau profonde : on nage, la tête hors de l'eau (lacs : profondeur inconnue, on nage toujours)
        bool water = WaterSurface.At(PosD, out double waterLevel);
        if (water)
        {
            Geo.ToLatLon(PosD.Normalized(), out double wla, out double wlo);
            double bottom = HeightProvider.SurfaceHeight(wla, wlo);
            double structure = Obstacles.SurfaceBelow(PosD);
            bool onStructure = structure > waterLevel + 0.3 && Altitude >= structure - 0.3; // pont, ponton, bateau garé
            bool deep = waterLevel - bottom > 1.2 || bottom >= waterLevel - 0.05; // mer profonde, ou lac / rivière (fond inconnu)
            water = deep && !onStructure;
        }
        if (water && Altitude < waterLevel + 0.3)
        {
            Swimming = true;
            Climbing = false;
            Grounded = false;
            _verticalSpeed = 0;
            double target = waterLevel - (Speed > 0.3 ? SwimDepth : TreadDepth);
            double a = Altitude;
            PosD = PosD.Normalized() * (Geo.Radius + a + (target - a) * Math.Min(1, dt * 4)); // flotte, suit les vagues
            GroundHeight = target;
            Crouching = false;
            Animate(dt);
            return;
        }
        Swimming = false;

        // Escalade : Espace contre un mur (ou un tronc) pour monter, jusqu'au toit
        Climbing = jump && blocked;
        if (Climbing) { _verticalSpeed = ClimbSpeed; Grounded = false; Crouching = false; }
        else if (jump && Grounded) { _verticalSpeed = JumpSpeed; Grounded = false; Crouching = false; }
        if (!Climbing) _verticalSpeed -= Gravity * dt;
        PosD += PosD.Normalized() * (_verticalSpeed * dt);
        // Plafond (dalle de l'étage au-dessus, dans un bâtiment) : on s'y cogne la tête
        double ceiling = Obstacles.CeilingAbove(PosD, Height + 1.5);
        if (Altitude + Height > ceiling)
        {
            PosD = PosD.Normalized() * (Geo.Radius + ceiling - Height);
            if (_verticalSpeed > 0) _verticalSpeed = 0;
            Climbing = false;
        }

        // Contrainte au sol (gravité radiale : « haut » = direction opposée au centre de la Terre)
        up = PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        GroundHeight = HeightProvider.SurfaceHeight(lat, lon);
        // Toits : on s'y pose comme sur le sol (et on en tombe en passant le bord)
        GroundHeight = Math.Max(GroundHeight, Obstacles.SurfaceBelow(PosD));
        double above = Altitude - GroundHeight;
        // On « colle » au sol dans les descentes plutôt que de décoller à chaque pas
        bool snap = Grounded && _verticalSpeed <= 0 && above < Math.Max(0.3, Speed * dt * 1.5);
        if (above < 0 || snap)
        {
            PosD = up * (Geo.Radius + GroundHeight);
            _verticalSpeed = 0;
            Grounded = true;
        }
        else if (above > 0.05) Grounded = false;

        Animate(dt);
    }

    void Animate(double dt)
    {
        if (_model == null) return;
        PlayerAppearance.SetMode(Swimming ? GameMode.Swimming : GameMode.Explore);
        var m = new CharacterMotion
        {
            Speed = Speed,
            Grounded = Grounded,
            VerticalSpeed = _verticalSpeed,
            Climbing = Climbing,
            Swimming = Swimming,
            Crouching = Crouching && Grounded,
            LookPitch = LookPitch,
        };
        _model.Update(m, dt);
    }

    /// <summary>Joue un geste (ouvrir une porte, ramasser, saluer…), voir <see cref="CharacterModel.PlayAction"/>.</summary>
    public void PlayAction(string anim) => _model?.PlayAction(anim);

    /// <summary>Place le nœud Godot par rapport à la caméra (le monde est rendu relatif à elle).</summary>
    public void Place(Vec3d cameraPos, Basis worldRotation)
    {
        var up = PosD.Normalized();
        Geo.LocalFrame(up, out var east, out var north);
        var fwd = north * Math.Cos(Yaw) + east * Math.Sin(Yaw);
        var right = fwd.Cross(up).Normalized();
        var basis = new Basis(right.ToVector3(), up.ToVector3(), (-fwd).ToVector3());
        Transform = new Transform3D(worldRotation * basis, worldRotation * (PosD - cameraPos).ToVector3());
    }
}
