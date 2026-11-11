using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Modèles 3D stylisés des véhicules (style « jouet » proche des modèles Kenney), construits en code à partir de
/// <see cref="ToyMesh"/> : aucun fichier à télécharger, aucune licence à gérer. Le pick-up et la moto sont des
/// modèles Kenney (CC0, assets/vehicles/kenney). Godot : avant = −Z, haut = +Y, origine au sol au centre du véhicule.
/// </summary>
public static class VehicleModels
{
    const string KenneyDir = "res://assets/vehicles/kenney/";
    static readonly Dictionary<string, StandardMaterial3D> Mats = new();

    public static StandardMaterial3D Mat(Color c, float rough = 0.55f, float metal = 0, float emit = 0, bool twoSided = false)
    {
        string key = $"{c}{rough}{metal}{emit}{twoSided}";
        if (Mats.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D { AlbedoColor = c, Roughness = rough, Metallic = metal };
        if (emit > 0) { m.EmissionEnabled = true; m.Emission = c; m.EmissionEnergyMultiplier = emit; }
        if (twoSided) m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        Mats[key] = m;
        return m;
    }

    static readonly Color Tyre = new(0.07f, 0.07f, 0.08f), RimC = new(0.78f, 0.79f, 0.8f), Dark = new(0.13f, 0.13f, 0.15f),
        GlassC = new(0.16f, 0.24f, 0.33f), WhiteC = new(0.94f, 0.94f, 0.92f), LightC = new(1f, 0.96f, 0.82f), TailC = new(0.75f, 0.06f, 0.05f),
        Wood = new(0.6f, 0.42f, 0.25f), Metal = new(0.62f, 0.63f, 0.66f);

    static StandardMaterial3D Glass => Mat(GlassC, 0.08f, 0.35f);

    public static void Build(Vehicle v, VehicleSpec s, VehicleRig rig)
    {
        var paint = Mat(s.Paint(), 0.32f, 0.25f);
        switch (s.Id)
        {
            case "car": Car(v, s, rig, paint, lowerH: 0.62f, cabLen: 0.56f, cabZ: 0.12f, cabH: 0.56f, taper: 0.2f); break;
            case "sport": Car(v, s, rig, paint, lowerH: 0.48f, cabLen: 0.42f, cabZ: 0.3f, cabH: 0.42f, taper: 0.3f); Spoiler(v, s, paint); break;
            case "suv": Car(v, s, rig, paint, lowerH: 0.78f, cabLen: 0.66f, cabZ: 0.35f, cabH: 0.72f, taper: 0.1f); RoofRails(v, s); break;
            case "van": Van(v, s, rig, paint); break;
            case "bus": Bus(v, s, rig, paint); break;
            case "truck": Truck(v, s, rig, paint); break;
            case "tractor": Tractor(v, s, rig, paint); break;
            case "pickup": Kenney(v, rig, "vehicle-truck-" + new[] { "red", "yellow", "green", "purple" }[GD.Randi() % 4], 1.75f, false); break;
            case "moto": Kenney(v, rig, "vehicle-motorcycle", 1.38f, true); break;
            case "bike": Bicycle(v, s, rig, paint); break;
            case "boat": MotorBoat(v, s, rig, paint); break;
            case "jetski": JetSki(v, s, rig, paint); break;
            case "sailboat": Sailboat(v, s, rig, paint); break;
            case "plane": LightPlane(v, s, rig, paint); break;
            case "airliner": Airliner(v, s, rig, paint); break;
            case "heli": Helicopter(v, s, rig, paint); break;
        }
        // les autres : siège sous les yeux du conducteur (environ 0,75 m plus bas, assis)
        if (rig.Rider == null)
            Rider(v, rig, new Vector3(s.Eye.X, s.Eye.Y - 0.75f, -s.Eye.Z), s.InAir ? CharacterSeat.Plane : CharacterSeat.Car);
    }

    // --- Outils

    static MeshInstance3D Part(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? rot = null)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos };
        if (rot is Vector3 r) mi.Rotation = r;
        parent.AddChild(mi);
        return mi;
    }

    static MeshInstance3D Box(Node3D parent, Vector3 size, float radius, Material mat, Vector3 pos, float taper = 0, float slant = 0, Vector3? rot = null)
        => Part(parent, ToyMesh.RoundedBox(size, radius, taper, slant), mat, pos, rot);

    static Node3D Pivot(Node3D parent, Vector3 pos)
    {
        var n = new Node3D { Position = pos };
        parent.AddChild(n);
        return n;
    }

    static MeshInstance3D Cyl(Node3D parent, float r0, float r1, float h, Material mat, Vector3 pos, Vector3? rot = null, int seg = 16)
        => Part(parent, new CylinderMesh { TopRadius = r0, BottomRadius = r1, Height = h, RadialSegments = seg }, mat, pos, rot);

    /// <summary>Tube entre deux points (cadre de vélo, haubans).</summary>
    static void Tube(Node3D parent, Vector3 a, Vector3 b, float r, Material mat)
    {
        var d = b - a;
        var mi = Part(parent, new CylinderMesh { TopRadius = r, BottomRadius = r, Height = d.Length(), RadialSegments = 8 }, mat, (a + b) / 2);
        mi.Basis = new Basis(new Quaternion(Vector3.Up, d.Normalized()));
    }

    /// <summary>Roue : pneu, jante, enjoliveur. Les roues avant sont sous un pivot de direction.</summary>
    static void Wheel(Node3D parent, VehicleRig rig, Vector3 pos, float r, float width, bool steer, Color? rim = null)
    {
        Node3D holder = parent;
        if (steer) { holder = Pivot(parent, pos); rig.SteerPivots.Add(holder); pos = Vector3.Zero; }
        var hub = Pivot(holder, pos);
        var axis = new Vector3(0, 0, Mathf.Pi / 2);
        Cyl(hub, r, r, width, Mat(Tyre, 0.9f), Vector3.Zero, axis, 20);
        Cyl(hub, r * 0.62f, r * 0.62f, width * 1.04f, Mat(rim ?? RimC, 0.3f, rim == null ? 0.7f : 0), Vector3.Zero, axis, 14);
        Part(hub, ToyMesh.RoundedBox(new Vector3(width * 1.08f, r * 0.9f, r * 0.2f), 0.02f), Mat(Dark, 0.5f), Vector3.Zero); // rayon : la rotation se voit
        rig.Wheels.Add((hub, r));
    }

    static void Lights(Node3D p, VehicleRig rig, float halfL, float y, float x, float frontZ = float.NaN)
    {
        float fz = float.IsNaN(frontZ) ? -halfL : frontZ;
        foreach (float sx in new[] { -x, x })
        {
            Box(p, new Vector3(0.34f, 0.15f, 0.08f), 0.04f, Mat(LightC, 0.2f, 0, 1.6f), new Vector3(sx, y, fz));
            Box(p, new Vector3(0.3f, 0.13f, 0.08f), 0.04f, Mat(TailC, 0.3f, 0, 0.4f), new Vector3(sx, y + 0.05f, halfL));
            rig.BrakeLights.Add(Box(p, new Vector3(0.31f, 0.14f, 0.06f), 0.04f, Mat(new Color(1, 0.08f, 0.05f), 0.3f, 0, 3f), new Vector3(sx, y + 0.05f, halfL + 0.02f)));
        }
    }

    /// <summary>Siège du conducteur (dessus du siège, repère du véhicule) : le personnage du joueur y est assis
    /// (voir <see cref="Vehicle"/>), dans la posture <paramref name="seatKind"/>.</summary>
    static void Rider(Node3D parent, VehicleRig rig, Vector3 seat, CharacterSeat seatKind)
    {
        rig.Rider = Pivot(parent, seat);
        rig.Rider.Visible = false;
        rig.Seat = seatKind;
    }

    // --- Route

    static void Car(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint, float lowerH, float cabLen, float cabZ, float cabH, float taper)
    {
        float L = (float)s.Length, W = (float)s.Width, wr = (float)s.WheelRadius;
        float bottom = wr * 0.62f;
        Box(v, new Vector3(W, lowerH, L), 0.2f, paint, new Vector3(0, bottom + lowerH / 2, 0));
        float cl = L * cabLen;
        float cy = bottom + lowerH + cabH / 2 - 0.04f;
        Box(v, new Vector3(W * 0.9f, cabH, cl), 0.16f, Glass, new Vector3(0, cy, cabZ), taper);
        float k = 1 - taper;
        Box(v, new Vector3(W * 0.9f * k + 0.03f, 0.08f, cl * k + 0.03f), 0.04f, paint, new Vector3(0, cy + cabH / 2, cabZ));
        // montants : le toit tient sur la caisse
        foreach (float sx in new[] { -1f, 1f })
            Box(v, new Vector3(0.08f, cabH, 0.14f), 0.03f, paint, new Vector3(sx * W * 0.45f * (1 - taper / 2), cy, cabZ), 0, 0, new Vector3(0, 0, sx * taper * 0.9f));
        Box(v, new Vector3(W + 0.04f, 0.22f, 0.28f), 0.1f, Mat(Dark, 0.6f), new Vector3(0, bottom + 0.1f, -L / 2 + 0.1f));
        Box(v, new Vector3(W + 0.04f, 0.22f, 0.28f), 0.1f, Mat(Dark, 0.6f), new Vector3(0, bottom + 0.1f, L / 2 - 0.1f));
        Lights(v, rig, L / 2, bottom + lowerH * 0.68f, W * 0.33f);
        float wz = (float)s.Wheelbase / 2, tx = (float)s.Track / 2;
        foreach (float sx in new[] { -tx, tx })
        {
            Wheel(v, rig, new Vector3(sx, wr, -wz), wr, wr * 0.75f, true);
            Wheel(v, rig, new Vector3(sx, wr, wz), wr, wr * 0.75f, false);
        }
    }

    static void Spoiler(Vehicle v, VehicleSpec s, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width;
        Box(v, new Vector3(W * 0.85f, 0.06f, 0.35f), 0.03f, paint, new Vector3(0, 1.05f, L / 2 - 0.25f));
        foreach (float x in new[] { -0.55f, 0.55f }) Box(v, new Vector3(0.06f, 0.22f, 0.12f), 0.02f, Mat(Dark), new Vector3(x, 0.93f, L / 2 - 0.25f));
    }

    static void RoofRails(Vehicle v, VehicleSpec s)
    {
        foreach (float x in new[] { -0.7f, 0.7f }) Box(v, new Vector3(0.05f, 0.06f, 2.3f), 0.02f, Mat(Metal, 0.3f, 0.7f), new Vector3(x, 2.0f, 0.4f));
    }

    static void Van(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width, wr = (float)s.WheelRadius, bottom = wr * 0.7f;
        float H = (float)s.Height;
        Box(v, new Vector3(W, H - bottom - 0.05f, L * 0.8f), 0.18f, paint, new Vector3(0, (H + bottom) / 2, L * 0.1f));         // caisse
        Box(v, new Vector3(W, 0.75f, L * 0.24f), 0.2f, paint, new Vector3(0, bottom + 0.38f, -L * 0.37f));                     // capot
        Box(v, new Vector3(W * 0.94f, H - bottom - 0.95f, L * 0.16f), 0.15f, Glass, new Vector3(0, bottom + 0.75f + (H - bottom - 0.95f) / 2, -L * 0.27f), 0.04f, 0.35f);
        Box(v, new Vector3(W + 0.02f, 0.5f, 1.0f), 0.08f, Glass, new Vector3(0, H - 0.75f, -L * 0.17f)); // vitres latérales
        Box(v, new Vector3(W + 0.04f, 0.24f, 0.28f), 0.1f, Mat(Dark), new Vector3(0, bottom + 0.1f, -L / 2 + 0.05f));
        Lights(v, rig, L / 2, bottom + 0.55f, W * 0.36f);
        float wz = (float)s.Wheelbase / 2, tx = (float)s.Track / 2;
        foreach (float sx in new[] { -tx, tx })
        {
            Wheel(v, rig, new Vector3(sx, wr, -wz - 0.25f), wr, 0.26f, true);
            Wheel(v, rig, new Vector3(sx, wr, wz - 0.25f), wr, 0.26f, false);
        }
    }

    static void Bus(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width, wr = (float)s.WheelRadius, bottom = 0.38f;
        Box(v, new Vector3(W, 1.15f, L), 0.25f, paint, new Vector3(0, bottom + 0.58f, 0));
        Box(v, new Vector3(W * 0.985f, 1.25f, L * 0.985f), 0.22f, Glass, new Vector3(0, bottom + 1.15f + 0.6f, 0));
        Box(v, new Vector3(W, 0.4f, L), 0.25f, paint, new Vector3(0, bottom + 2.4f + 0.18f, 0));
        for (int i = -4; i <= 4; i++) // montants entre les fenêtres
            foreach (float sx in new[] { -1f, 1f })
                Box(v, new Vector3(0.06f, 1.2f, 0.16f), 0.02f, paint, new Vector3(sx * W * 0.495f, bottom + 1.75f, i * L / 10f));
        Box(v, new Vector3(0.06f, 2.0f, 1.0f), 0.04f, Mat(Dark), new Vector3(W / 2 + 0.01f, bottom + 1.1f, -L / 2 + 1.4f)); // porte avant
        Box(v, new Vector3(0.06f, 2.0f, 1.2f), 0.04f, Mat(Dark), new Vector3(W / 2 + 0.01f, bottom + 1.1f, 0.6f));
        Box(v, new Vector3(1.6f, 0.25f, 0.06f), 0.03f, Mat(new Color(1, 0.6f, 0.1f), 0.3f, 0, 2f), new Vector3(0, bottom + 2.45f, -L / 2 - 0.01f)); // girouette
        Lights(v, rig, L / 2, bottom + 0.55f, W * 0.38f);
        float zf = -L / 2 + 2.6f, zr = zf + (float)s.Wheelbase;
        foreach (float sx in new[] { -1.05f, 1.05f })
        {
            Wheel(v, rig, new Vector3(sx, wr, zf), wr, 0.32f, true);
            Wheel(v, rig, new Vector3(sx, wr, zr), wr, 0.32f, false);
        }
    }

    static void Truck(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width, wr = (float)s.WheelRadius;
        float cabZ = -L / 2 + 1.25f;
        Box(v, new Vector3(W, 1.5f, 2.4f), 0.22f, paint, new Vector3(0, 0.6f + 0.75f, cabZ));
        Box(v, new Vector3(W * 0.96f, 1.0f, 2.0f), 0.18f, Glass, new Vector3(0, 2.1f + 0.48f, cabZ + 0.1f), 0.06f, 0.2f);
        Box(v, new Vector3(W, 0.3f, 2.2f), 0.15f, paint, new Vector3(0, 3.15f, cabZ + 0.2f));
        Box(v, new Vector3(W * 0.6f, 0.35f, L - 2.0f), 0.05f, Mat(Dark), new Vector3(0, 0.7f, 1.0f)); // châssis
        var cargo = Mat(new Color(0.88f, 0.88f, 0.86f), 0.6f);
        Box(v, new Vector3(W + 0.02f, (float)s.Height - 1.05f, L - 2.9f), 0.12f, cargo, new Vector3(0, 1.05f + ((float)s.Height - 1.05f) / 2, 1.42f));
        Box(v, new Vector3(W + 0.04f, 0.4f, L - 3.2f), 0.06f, paint, new Vector3(0, 1.9f, 1.42f)); // bande de couleur
        Lights(v, rig, L / 2, 0.75f, W * 0.38f, -L / 2 + 0.02f);
        float zf = cabZ + 0.05f;
        foreach (float sx in new[] { -1.0f, 1.0f })
        {
            Wheel(v, rig, new Vector3(sx, wr, zf), wr, 0.34f, true);
            Wheel(v, rig, new Vector3(sx, wr, L / 2 - 2.4f), wr, 0.42f, false);
            Wheel(v, rig, new Vector3(sx, wr, L / 2 - 1.25f), wr, 0.42f, false);
        }
    }

    static void Tractor(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        Box(v, new Vector3(0.95f, 0.95f, 2.1f), 0.18f, paint, new Vector3(0, 1.25f, -0.95f), 0.08f);              // capot
        Box(v, new Vector3(0.9f, 0.5f, 0.12f), 0.04f, Mat(Dark), new Vector3(0, 1.2f, -2.02f));                     // calandre
        Box(v, new Vector3(1.2f, 0.6f, 1.6f), 0.12f, paint, new Vector3(0, 1.3f, 0.75f));                           // assise
        Box(v, new Vector3(1.55f, 1.35f, 1.5f), 0.12f, Glass, new Vector3(0, 2.25f, 0.75f));                        // cabine
        Box(v, new Vector3(1.7f, 0.12f, 1.7f), 0.05f, paint, new Vector3(0, 2.98f, 0.75f));                          // toit
        foreach (float sx in new[] { -0.75f, 0.75f }) foreach (float z in new[] { 0.05f, 1.45f })
                Box(v, new Vector3(0.08f, 1.35f, 0.08f), 0.02f, Mat(Dark), new Vector3(sx, 2.25f, z));
        Cyl(v, 0.07f, 0.07f, 1.2f, Mat(Dark, 0.4f, 0.6f), new Vector3(0.35f, 2.1f, -1.6f));                          // échappement
        Lights(v, rig, 1.6f, 1.45f, 0.35f, -2.05f);
        foreach (float sx in new[] { -0.98f, 0.98f })
        {
            var yellow = new Color(0.95f, 0.75f, 0.1f); // jantes jaunes de tracteur
            Wheel(v, rig, new Vector3(sx, 0.8f, 0.75f), 0.8f, 0.5f, false, yellow);
            Wheel(v, rig, new Vector3(sx * 0.85f, 0.45f, -1.55f), 0.45f, 0.3f, true, yellow);
        }
    }

    /// <summary>Modèle Kenney (CC0) : roues repérées par leur nom, mises sous des pivots pour tourner et braquer.</summary>
    static void Kenney(Vehicle v, VehicleRig rig, string file, float scale, bool twoWheels)
    {
        var scene = GD.Load<PackedScene>(KenneyDir + file + ".glb");
        if (scene == null) { GD.PrintErr($"[Véhicules] modèle introuvable : {file}"); return; }
        var root = scene.Instantiate<Node3D>();
        // pivot d'inclinaison (deux-roues) séparé : Animate réécrit sa rotation, le demi-tour du modèle est en dessous
        var lean = Pivot(v, Vector3.Zero);
        if (twoWheels) rig.Lean = lean;
        var holder = Pivot(lean, Vector3.Zero);
        holder.Scale = Vector3.One * scale;
        holder.Rotation = new Vector3(0, Mathf.Pi, 0); // les modèles Kenney regardent vers +Z
        holder.AddChild(root);
        var nodes = new List<Node3D>();
        Collect(root, nodes);
        Node3D steer = null;
        foreach (var n in nodes)
        {
            string name = n.Name.ToString().ToLowerInvariant();
            if (!name.StartsWith("wheel") && name != "fork") continue;
            bool front = name.Contains("front") || name == "fork";
            var parent = (Node3D)n.GetParent();
            var t = n.Transform;
            Node3D pivotParent = parent;
            if (front && twoWheels)
            {
                // la fourche et la roue avant braquent ensemble autour de la colonne de direction
                if (steer == null) { var fork = nodes.Find(x => x.Name.ToString().ToLowerInvariant() == "fork"); steer = Pivot(parent, fork != null ? fork.Position : t.Origin); rig.SteerPivots.Add(steer); }
                pivotParent = steer;
                t.Origin -= steer.Position;
            }
            else if (front)
            {
                var sp = Pivot(parent, t.Origin);
                rig.SteerPivots.Add(sp);
                pivotParent = sp;
                t.Origin = Vector3.Zero;
            }
            parent.RemoveChild(n);
            if (name == "fork") { n.Transform = t; pivotParent.AddChild(n); continue; }
            var hub = Pivot(pivotParent, t.Origin);
            t.Origin = Vector3.Zero;
            n.Transform = t;
            hub.AddChild(n);
            rig.Wheels.Add((hub, -0.3f * scale)); // rayon négatif : le modèle est retourné (demi-tour), la roue tourne en sens inverse
        }
        if (twoWheels) Rider(lean, rig, new Vector3(0, 0.8f, 0.15f), CharacterSeat.Bike); // selle, repère du véhicule
    }

    static void Collect(Node n, List<Node3D> list)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is Node3D n3) list.Add(n3);
            Collect(c, list);
        }
    }

    static void Bicycle(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        var lean = Pivot(v, Vector3.Zero);
        rig.Lean = lean;
        var tyre = Mat(Tyre, 0.9f);
        var steel = Mat(Metal, 0.3f, 0.9f);
        var dark = Mat(Dark, 0.7f);
        var torus = new TorusMesh { InnerRadius = 0.30f, OuterRadius = 0.35f, Rings = 24, RingSegments = 8 };
        Node3D WheelAt(Node3D parent, Vector3 p)
        {
            var hub = Pivot(parent, p);
            Part(hub, torus, tyre, Vector3.Zero, new Vector3(0, 0, Mathf.Pi / 2));
            Part(hub, new BoxMesh { Size = new Vector3(0.02f, 0.6f, 0.02f) }, steel, Vector3.Zero);
            Part(hub, new BoxMesh { Size = new Vector3(0.02f, 0.02f, 0.6f) }, steel, Vector3.Zero);
            rig.Wheels.Add((hub, 0.34f));
            return hub;
        }
        var head = new Vector3(0, 0.9f, -0.42f);
        var bars = Pivot(lean, head);
        rig.SteerPivots.Add(bars);
        WheelAt(bars, new Vector3(0, 0.35f, -0.53f) - head);
        WheelAt(lean, new Vector3(0, 0.35f, 0.53f));
        var crank = new Vector3(0, 0.33f, 0.02f);
        var seat = new Vector3(0, 0.95f, 0.2f);
        Tube(lean, crank, seat, 0.025f, paint); Tube(lean, crank, head, 0.03f, paint); Tube(lean, seat, head, 0.025f, paint);
        Tube(lean, crank, new Vector3(0, 0.35f, 0.53f), 0.02f, paint); Tube(lean, seat, new Vector3(0, 0.35f, 0.53f), 0.02f, paint);
        Tube(bars, Vector3.Zero, new Vector3(0, -0.55f, -0.11f), 0.022f, steel);
        Part(bars, new BoxMesh { Size = new Vector3(0.56f, 0.03f, 0.03f) }, dark, new Vector3(0, 0.14f, 0.04f));
        Tube(bars, Vector3.Zero, new Vector3(0, 0.14f, 0.04f), 0.02f, steel);
        Box(lean, new Vector3(0.14f, 0.05f, 0.26f), 0.02f, dark, seat + new Vector3(0, 0.03f, 0));
        Rider(lean, rig, seat + new Vector3(0, 0.05f, 0.02f), CharacterSeat.Bike);
    }

    // --- Sur l'eau

    static Mesh Hull(string key, float L, float halfW, float deck, float keel, float bowRise = 0.25f)
        => ToyMesh.Loft(key, -L / 2, L / 2, t =>
        {
            float w = halfW * Mathf.Pow(Mathf.Sin(Mathf.Min(1, t * 1.7f + 0.02f) * Mathf.Pi / 2), 0.6f) * (t > 0.92f ? 1 - (t - 0.92f) * 0.6f : 1);
            float d = deck + bowRise * Mathf.Max(0, 1 - t * 2.5f);
            float k = keel * (1 - 0.35f * t);
            return ToyMesh.HullSection(w, d, k);
        }, 22);

    static void MotorBoat(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width;
        Part(v, Hull("hull-boat", L, W / 2, 0.6f, 0.45f), Mat(WhiteC, 0.35f, 0, 0, true), Vector3.Zero);
        Part(v, Hull("hull-boat-stripe", L * 0.995f, W / 2 + 0.01f, 0.25f, 0.2f), paint, new Vector3(0, 0.06f, 0)); // bande de couleur
        Box(v, new Vector3(W - 0.35f, 0.06f, L * 0.6f), 0.02f, Mat(Wood, 0.7f), new Vector3(0, 0.6f, 0.45f));        // pont
        Box(v, new Vector3(0.65f, 0.75f, 0.55f), 0.1f, paint, new Vector3(-0.45f, 0.98f, -0.25f));                     // poste de pilotage
        Box(v, new Vector3(W - 0.25f, 0.45f, 0.08f), 0.03f, Glass, new Vector3(0, 1.15f, -0.65f), 0, 0, new Vector3(-0.5f, 0, 0)); // pare-brise
        Box(v, new Vector3(1.8f, 0.4f, 0.6f), 0.12f, Mat(WhiteC, 0.8f), new Vector3(0, 0.82f, 1.9f));                  // banquette
        Box(v, new Vector3(0.5f, 0.4f, 0.5f), 0.1f, Mat(WhiteC, 0.8f), new Vector3(-0.45f, 0.82f, 0.45f));            // siège
        var motor = Pivot(v, new Vector3(0, 0.65f, L / 2 - 0.05f));
        rig.Motor = motor;
        Box(motor, new Vector3(0.45f, 0.7f, 0.5f), 0.12f, Mat(Dark, 0.4f), new Vector3(0, 0.25f, 0.15f));
        Box(motor, new Vector3(0.14f, 1.0f, 0.22f), 0.04f, Mat(Dark, 0.4f), new Vector3(0, -0.45f, 0.15f));
        var prop = Pivot(motor, new Vector3(0, -0.9f, 0.3f));
        Box(prop, new Vector3(0.44f, 0.08f, 0.03f), 0.02f, Mat(Metal, 0.3f, 0.9f), Vector3.Zero);
        rig.Spinners.Add((prop, Vector3.Back, 1));
        Rider(v, rig, new Vector3(-0.45f, 0.96f, 0.5f), CharacterSeat.Boat);
    }

    static void JetSki(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width;
        Part(v, Hull("hull-jetski", L, W / 2, 0.45f, 0.3f, 0.15f), Mat(WhiteC, 0.35f, 0, 0, true), Vector3.Zero);
        Box(v, new Vector3(W * 0.8f, 0.4f, L * 0.45f), 0.18f, paint, new Vector3(0, 0.6f, -L * 0.18f), 0.25f);       // capot
        Box(v, new Vector3(0.42f, 0.22f, 1.2f), 0.1f, Mat(Dark, 0.8f), new Vector3(0, 0.62f, 0.45f));                 // selle
        var bars = Pivot(v, new Vector3(0, 0.95f, -0.35f));
        rig.SteerPivots.Add(bars);
        Box(bars, new Vector3(0.7f, 0.05f, 0.05f), 0.02f, Mat(Dark), Vector3.Zero);
        Rider(v, rig, new Vector3(0, 0.75f, 0.4f), CharacterSeat.Bike);
    }

    static void Sailboat(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        float L = (float)s.Length, W = (float)s.Width;
        Part(v, Hull("hull-sail", L, W / 2, 0.75f, 0.6f, 0.2f), Mat(WhiteC, 0.35f, 0, 0, true), Vector3.Zero);
        Part(v, Hull("hull-sail-stripe", L * 0.995f, W / 2 + 0.01f, 0.35f, 0.25f, 0.2f), paint, new Vector3(0, 0.08f, 0));
        Box(v, new Vector3(W - 0.4f, 0.06f, L * 0.75f), 0.02f, Mat(Wood, 0.7f), new Vector3(0, 0.76f, 0.3f));
        Box(v, new Vector3(1.8f, 0.6f, 2.6f), 0.2f, Mat(WhiteC, 0.5f), new Vector3(0, 1.05f, -0.4f), 0.15f);        // rouf
        Box(v, new Vector3(1.6f, 0.2f, 2.3f), 0.08f, Glass, new Vector3(0, 1.12f, -0.4f));
        Box(v, new Vector3(0.25f, 1.4f, 1.2f), 0.06f, Mat(Dark), new Vector3(0, -0.9f, 0.3f));                       // quille
        float mastZ = -1.2f, mastH = 12.5f;
        Cyl(v, 0.07f, 0.1f, mastH, Mat(Metal, 0.3f, 0.8f), new Vector3(0, 0.8f + mastH / 2, mastZ), null, 10);
        var boom = Pivot(v, new Vector3(0, 1.9f, mastZ));
        rig.Boom = boom;
        Cyl(boom, 0.06f, 0.06f, 3.8f, Mat(Metal, 0.3f, 0.8f), new Vector3(0, 0, 1.9f), new Vector3(Mathf.Pi / 2, 0, 0), 8);
        var sail = Mat(new Color(0.97f, 0.97f, 0.95f), 0.9f, 0, 0, true);
        Part(boom, Triangle(new Vector3(0, 0.1f, 0.05f), new Vector3(0, mastH - 1.4f, 0.05f), new Vector3(0, 0.1f, 3.7f)), sail, Vector3.Zero);
        Part(v, Triangle(new Vector3(0, 0.95f, -L / 2 + 0.3f), new Vector3(0, mastH - 0.5f, mastZ - 0.05f), new Vector3(0, 1.5f, mastZ - 0.4f)), sail, Vector3.Zero);
        Rider(v, rig, new Vector3(0.6f, 1.1f, L / 2 - 1.2f), CharacterSeat.Boat);
    }

    static Mesh Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.GenerateNormals();
        return st.Commit();
    }

    // --- Dans les airs

    static void LightPlane(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        var white = Mat(WhiteC, 0.35f, 0.1f, 0, true);
        Part(v, ToyMesh.Loft("fus-light", -3.9f, 4.7f, t =>
        {
            float w = t < 0.12f ? Mathf.Lerp(0.38f, 0.6f, t / 0.12f) : t < 0.45f ? 0.62f : Mathf.Lerp(0.62f, 0.12f, (t - 0.45f) / 0.55f);
            float h = t < 0.12f ? Mathf.Lerp(0.4f, 0.68f, t / 0.12f) : t < 0.45f ? 0.72f : Mathf.Lerp(0.72f, 0.16f, (t - 0.45f) / 0.55f);
            float cy = 1.55f + Mathf.Max(0, t - 0.45f) * 0.7f;
            return ToyMesh.Ellipse(w, h, cy);
        }), white, Vector3.Zero);
        Part(v, ToyMesh.Loft("cowl-light", -3.92f, -2.6f, t => ToyMesh.Ellipse(Mathf.Lerp(0.4f, 0.64f, t), Mathf.Lerp(0.42f, 0.7f, t), 1.55f)), paint, Vector3.Zero);
        Box(v, new Vector3(1.18f, 0.55f, 1.7f), 0.2f, Glass, new Vector3(0, 2.12f, -0.75f), 0.12f, 0.15f);             // verrière
        Box(v, new Vector3(1.28f, 0.1f, 3.6f), 0.05f, paint, new Vector3(0, 1.35f, 0.9f));                              // bande
        // aile haute, saumons colorés, haubans
        Box(v, new Vector3(11f, 0.17f, 1.55f), 0.07f, white, new Vector3(0, 2.48f, -0.45f));
        foreach (float x in new[] { -5.25f, 5.25f }) Box(v, new Vector3(0.55f, 0.18f, 1.57f), 0.07f, paint, new Vector3(x, 2.48f, -0.45f));
        rig.AileronL = Pivot(v, new Vector3(-3.8f, 2.48f, 0.32f));
        Box(rig.AileronL, new Vector3(2.2f, 0.07f, 0.3f), 0.02f, white, new Vector3(0, 0, 0.15f));
        rig.AileronR = Pivot(v, new Vector3(3.8f, 2.48f, 0.32f));
        Box(rig.AileronR, new Vector3(2.2f, 0.07f, 0.3f), 0.02f, white, new Vector3(0, 0, 0.15f));
        foreach (float sx in new[] { -1f, 1f }) Tube(v, new Vector3(sx * 0.6f, 1.2f, -0.3f), new Vector3(sx * 2.4f, 2.42f, -0.45f), 0.035f, Mat(Metal, 0.3f, 0.8f));
        // empennage
        Box(v, new Vector3(3.4f, 0.08f, 0.85f), 0.04f, white, new Vector3(0, 2.0f, 4.15f), 0.2f);
        rig.Elevator = Pivot(v, new Vector3(0, 2.0f, 4.58f));
        Box(rig.Elevator, new Vector3(3.2f, 0.06f, 0.36f), 0.02f, paint, new Vector3(0, 0, 0.17f));
        Box(v, new Vector3(0.1f, 1.35f, 1.1f), 0.04f, white, new Vector3(0, 2.7f, 4.05f), 0.35f, 0.45f);
        rig.RudderNode = Pivot(v, new Vector3(0, 2.62f, 4.62f));
        Box(rig.RudderNode, new Vector3(0.07f, 1.2f, 0.35f), 0.02f, paint, new Vector3(0, 0, 0.17f));
        // hélice
        var prop = Pivot(v, new Vector3(0, 1.55f, -3.98f));
        Part(prop, new SphereMesh { Radius = 0.2f, Height = 0.4f }, paint, Vector3.Zero);
        Box(prop, new Vector3(1.9f, 0.16f, 0.04f), 0.03f, Mat(Dark, 0.5f), Vector3.Zero);
        rig.Spinners.Add((prop, Vector3.Back, 1));
        // train tricycle carené
        foreach (float sx in new[] { -1.25f, 1.25f })
        {
            Wheel(v, rig, new Vector3(sx, 0.27f, 0.3f), 0.27f, 0.16f, false);
            Box(v, new Vector3(0.24f, 0.36f, 0.75f), 0.12f, paint, new Vector3(sx, 0.36f, 0.3f));
            Tube(v, new Vector3(sx * 0.4f, 1.1f, 0.3f), new Vector3(sx, 0.45f, 0.3f), 0.04f, Mat(Metal, 0.3f, 0.8f));
        }
        Wheel(v, rig, new Vector3(0, 0.22f, -2.4f), 0.22f, 0.12f, true);
        Tube(v, new Vector3(0, 1.0f, -2.45f), new Vector3(0, 0.25f, -2.4f), 0.04f, Mat(Metal, 0.3f, 0.8f));
    }

    static void Airliner(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        var white = Mat(WhiteC, 0.3f, 0.15f, 0, true);
        var grey = Mat(new Color(0.75f, 0.77f, 0.8f), 0.4f, 0.3f);
        float cy = 4.4f, R = 2.0f;
        Part(v, ToyMesh.Loft("fus-jet", -19f, 19f, t =>
        {
            float r = t < 0.08f ? R * Mathf.Sqrt(Mathf.Max(0.02f, t / 0.08f)) : t < 0.78f ? R : Mathf.Lerp(R, 0.35f, (t - 0.78f) / 0.22f);
            float y = cy + (t > 0.78f ? (t - 0.78f) / 0.22f * 1.5f : 0) - (t < 0.08f ? (1 - t / 0.08f) * 0.6f : 0);
            return ToyMesh.Ellipse(r, r * 1.05f, y, 20);
        }, 24), white, Vector3.Zero);
        // fenêtres et poste de pilotage
        foreach (float sx in new[] { -1f, 1f }) Box(v, new Vector3(0.05f, 0.35f, 26f), 0.03f, Mat(Dark, 0.3f), new Vector3(sx * (R + 0.02f), cy + 0.55f, -1f));
        Box(v, new Vector3(2.3f, 0.6f, 1.2f), 0.2f, Mat(Dark, 0.2f, 0.3f), new Vector3(0, cy + 0.85f, -16.9f), 0.2f, 0.4f);
        foreach (float sx in new[] { -1f, 1f }) Box(v, new Vector3(0.06f, 0.55f, 30f), 0.03f, paint, new Vector3(sx * (R + 0.02f), cy - 0.35f, 0)); // bande
        // ailes en flèche, légèrement relevées
        foreach (float sx in new[] { -1f, 1f })
        {
            var wing = Pivot(v, new Vector3(sx * (R + 7.2f), cy - 1.3f + 0.5f, 2.5f));
            wing.Rotation = new Vector3(0, sx * 0.45f, sx * 0.08f);
            Box(wing, new Vector3(15f, 0.45f, 4.6f), 0.2f, grey, Vector3.Zero, 0.35f);
            Box(wing, new Vector3(0.4f, 2.0f, 1.6f), 0.15f, paint, new Vector3(sx * 7.4f, 1.0f, 1.0f), 0.3f, 0.6f); // winglet
            // réacteur sous l'aile
            var eng = Pivot(v, new Vector3(sx * 6.0f, cy - 2.6f, -1.2f));
            Part(eng, ToyMesh.Loft("nacelle", -2.2f, 2.2f, t => ToyMesh.Ellipse(Mathf.Lerp(1.0f, 0.65f, t * t), Mathf.Lerp(1.0f, 0.65f, t * t), 0, 18), 8), Mat(new Color(0.85f, 0.86f, 0.88f), 0.3f, 0.4f, 0, true), Vector3.Zero);
            Cyl(eng, 0.88f, 0.88f, 0.05f, Mat(Dark, 0.4f, 0.5f), new Vector3(0, 0, -2.18f), new Vector3(Mathf.Pi / 2, 0, 0), 18);
            var fan = Pivot(eng, new Vector3(0, 0, -2.15f));
            for (int i = 0; i < 3; i++) Box(fan, new Vector3(1.6f, 0.18f, 0.04f), 0.02f, Mat(Metal, 0.3f, 0.9f), Vector3.Zero, 0, 0, new Vector3(0, 0, i * Mathf.Pi / 3));
            rig.Spinners.Add((fan, Vector3.Back, 1));
            Tube(v, new Vector3(sx * 6.0f, cy - 1.7f, -0.5f), new Vector3(sx * 6.0f, cy - 0.9f, 0.5f), 0.25f, grey); // mât
        }
        // empennage
        Box(v, new Vector3(0.5f, 6.5f, 5.5f), 0.2f, paint, new Vector3(0, cy + 4.5f, 16.3f), 0.45f, 2.6f);
        rig.RudderNode = Pivot(v, new Vector3(0, cy + 4.0f, 19.2f));
        foreach (float sx in new[] { -1f, 1f })
        {
            var stab = Pivot(v, new Vector3(sx * 3.4f, cy + 1.2f, 17.0f));
            stab.Rotation = new Vector3(0, sx * 0.5f, 0);
            Box(stab, new Vector3(6.5f, 0.3f, 2.6f), 0.12f, grey, Vector3.Zero, 0.35f);
        }
        rig.Elevator = Pivot(v, new Vector3(0, cy + 1.2f, 18.5f));
        // train d'atterrissage
        var strut = Mat(Metal, 0.3f, 0.8f);
        Wheel(v, rig, new Vector3(0, 0.55f, -15.5f), 0.55f, 0.35f, true);
        Tube(v, new Vector3(0, cy - 1.8f, -15.5f), new Vector3(0, 0.6f, -15.5f), 0.15f, strut);
        foreach (float sx in new[] { -3.2f, 3.2f })
        {
            foreach (float dz in new[] { 1.3f, 2.7f })
                foreach (float dx in new[] { -0.45f, 0.45f })
                    Wheel(v, rig, new Vector3(sx + dx, 0.6f, dz), 0.6f, 0.38f, false);
            Tube(v, new Vector3(sx, cy - 1.6f, 2.0f), new Vector3(sx, 0.7f, 2.0f), 0.2f, strut);
        }
    }

    static void Helicopter(Vehicle v, VehicleSpec s, VehicleRig rig, Material paint)
    {
        var bodyMat = new StandardMaterial3D { AlbedoColor = ((StandardMaterial3D)paint).AlbedoColor, Roughness = 0.32f, Metallic = 0.25f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        Part(v, ToyMesh.Loft("heli-body", -2.4f, 2.0f, t =>
        {
            float w = t < 0.35f ? Mathf.Lerp(0.35f, 0.9f, Mathf.Sqrt(t / 0.35f)) : Mathf.Lerp(0.9f, 0.45f, (t - 0.35f) / 0.65f);
            float h = t < 0.35f ? Mathf.Lerp(0.4f, 0.95f, Mathf.Sqrt(t / 0.35f)) : Mathf.Lerp(0.95f, 0.55f, (t - 0.35f) / 0.65f);
            return ToyMesh.Ellipse(w, h, 1.5f + t * 0.25f);
        }), bodyMat, Vector3.Zero);
        Box(v, new Vector3(1.6f, 1.0f, 1.6f), 0.45f, Glass, new Vector3(0, 1.85f, -1.35f), 0.18f, 0.3f);             // bulle vitrée
        Part(v, ToyMesh.Loft("heli-boom", 1.6f, 7.3f, t => ToyMesh.Ellipse(Mathf.Lerp(0.3f, 0.12f, t), Mathf.Lerp(0.32f, 0.14f, t), 1.95f + t * 0.35f, 12), 6), bodyMat, Vector3.Zero);
        Box(v, new Vector3(0.1f, 1.3f, 0.8f), 0.04f, paint, new Vector3(0, 2.75f, 7.1f), 0.3f, 0.35f);                  // dérive
        Box(v, new Vector3(1.6f, 0.07f, 0.45f), 0.03f, paint, new Vector3(0, 2.2f, 6.4f));                            // stabilisateur
        Box(v, new Vector3(0.7f, 0.45f, 1.4f), 0.15f, Mat(Dark, 0.5f), new Vector3(0, 2.55f, 0.2f));                  // turbine
        // rotor principal (4 pales) et rotor de queue
        Cyl(v, 0.08f, 0.1f, 0.5f, Mat(Metal, 0.3f, 0.8f), new Vector3(0, 2.95f, 0));
        var rotor = Pivot(v, new Vector3(0, 3.2f, 0));
        for (int i = 0; i < 4; i++) Box(rotor, new Vector3(5.2f, 0.06f, 0.3f), 0.025f, Mat(Dark, 0.5f), new Vector3(0, 0, 0), 0, 0, new Vector3(0, i * Mathf.Pi / 2, 0)).Position = Vector3.Zero;
        Part(rotor, new SphereMesh { Radius = 0.22f, Height = 0.3f }, Mat(Metal, 0.3f, 0.8f), Vector3.Zero);
        rig.Spinners.Add((rotor, Vector3.Up, 1));
        var tail = Pivot(v, new Vector3(0.18f, 2.5f, 7.25f));
        for (int i = 0; i < 2; i++) Box(tail, new Vector3(0.04f, 1.2f, 0.14f), 0.015f, Mat(Dark, 0.5f), Vector3.Zero, 0, 0, new Vector3(i * Mathf.Pi / 2, 0, 0));
        rig.Spinners.Add((tail, Vector3.Right, 4.5f));
        // patins
        foreach (float sx in new[] { -0.95f, 0.95f })
        {
            Cyl(v, 0.06f, 0.06f, 3.6f, Mat(Metal, 0.3f, 0.8f), new Vector3(sx, 0.07f, -0.2f), new Vector3(Mathf.Pi / 2, 0, 0), 8);
            Tube(v, new Vector3(sx, 0.07f, -1.0f), new Vector3(sx * 0.55f, 0.9f, -0.9f), 0.045f, Mat(Metal, 0.3f, 0.8f));
            Tube(v, new Vector3(sx, 0.07f, 0.7f), new Vector3(sx * 0.55f, 0.9f, 0.6f), 0.045f, Mat(Metal, 0.3f, 0.8f));
        }
    }
}
