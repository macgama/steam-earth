using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>Famille de conduite : chaque famille a sa physique (arcade) et son tableau de bord.</summary>
public enum VehicleFamily { Road, Bicycle, Motorbike, Boat, Sailboat, Plane, Jet, Helicopter }

/// <summary>Bruit du moteur : échantillon Kenney (CC0) dont la hauteur suit le régime, ou son synthétisé.</summary>
public enum EngineSound { None, Car, Truck, Motorbike, Outboard, Propeller, Jet, Rotor, Bicycle, Sail }

/// <summary>
/// Fiche d'un modèle de véhicule : catégorie du menu, physique arcade (vitesse maxi, accélération, freinage, virage),
/// dimensions, place du conducteur (vue à la 1re personne), son. Le catalogue est dans <see cref="VehicleCatalog"/>.
/// </summary>
public sealed class VehicleSpec
{
    public string Id, Name, Category;
    public VehicleFamily Family;
    public EngineSound Sound;
    public Func<Color> Paint = () => new Color(0.8f, 0.2f, 0.2f);

    // Physique arcade (m, kg, m/s, m/s²)
    public double Mass = 1200;
    public double TopSpeed = 45;       // vitesse maximale sur le plat
    public double Accel = 3.5;         // accélération au départ (diminue vers la vitesse maximale)
    public double Brake = 9;           // décélération au freinage
    public double Reverse = 7;         // vitesse maximale en marche arrière
    public double Grip = 1.1;          // adhérence latérale (g) : limite du virage
    public double MaxSteer = 0.6;      // braquage maximal à l'arrêt (rad)
    public double SlopeFactor = 0.6;   // part de la pente ressentie (1 = réaliste, vélo plus sensible)
    public int Gears = 5;

    // Dimensions
    public double Length = 4, Width = 1.8, Height = 1.5;
    public double Wheelbase = 2.5, Track = 1.5, WheelRadius = 0.32;

    // Bateau / avion / hélicoptère
    public double TurnRate = 0.5;      // bateau, hélicoptère : vitesse de rotation maximale (rad/s)
    public double StallSpeed = 25;     // avion : décrochage (m/s)
    public double PitchRate = 0.6, RollRate = 1.3; // avion : rad/s
    public double ClimbRate = 8;       // hélicoptère : vitesse verticale (m/s)
    public double Draft = 0.4;         // bateau : enfoncement de la coque

    // Caméra et place du conducteur (repère du véhicule : droite, haut, avant ; origine au sol)
    public double CamDistance = 7, CamHeight = 1.3, CamPitch = -0.22;
    public Vector3 Eye = new(-0.35f, 1.15f, 0.1f);

    public bool InAir => Family is VehicleFamily.Plane or VehicleFamily.Jet or VehicleFamily.Helicopter;
    public bool OnWater => Family is VehicleFamily.Boat or VehicleFamily.Sailboat;
    public bool TwoWheels => Family is VehicleFamily.Bicycle or VehicleFamily.Motorbike;

    public Vehicle Create() => Family switch
    {
        VehicleFamily.Boat or VehicleFamily.Sailboat => new Boat(this),
        VehicleFamily.Plane or VehicleFamily.Jet => new Plane(this),
        VehicleFamily.Helicopter => new Helicopter(this),
        _ => new RoadVehicle(this),
    };
}

/// <summary>Tous les véhicules du menu G, par catégorie.</summary>
public static class VehicleCatalog
{
    public static readonly string[] Categories = { "Route", "Deux-roues", "Sur l'eau", "Dans les airs" };

    static Func<Color> Pick(params Color[] c) => () => c[(int)(GD.Randi() % (uint)c.Length)];
    static readonly Color Red = new(0.78f, 0.13f, 0.12f), Blue = new(0.14f, 0.35f, 0.72f), White = new(0.93f, 0.93f, 0.9f),
        Black = new(0.12f, 0.12f, 0.14f), Green = new(0.18f, 0.5f, 0.3f), Yellow = new(0.95f, 0.75f, 0.15f), Orange = new(0.92f, 0.45f, 0.12f),
        Silver = new(0.68f, 0.7f, 0.72f), Teal = new(0.1f, 0.55f, 0.6f);

    public static readonly List<VehicleSpec> All = new()
    {
        // --- Route
        new() { Id = "car", Name = "Citadine", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Car, Paint = Pick(Red, Blue, White, Yellow, Teal, Silver),
                Mass = 1100, TopSpeed = 46, Accel = 3.4, Brake = 9, Reverse = 8, Grip = 1.05, Gears = 5,
                Length = 3.8, Width = 1.7, Height = 1.5, Wheelbase = 2.45, Track = 1.45, WheelRadius = 0.3, Eye = new(-0.36f, 1.12f, 0.15f) },
        new() { Id = "sport", Name = "Voiture de sport", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Car, Paint = Pick(Red, Yellow, Orange, Black, Blue),
                Mass = 1350, TopSpeed = 75, Accel = 7.5, Brake = 11.5, Reverse = 8, Grip = 1.35, MaxSteer = 0.55, Gears = 7,
                Length = 4.4, Width = 1.95, Height = 1.2, Wheelbase = 2.65, Track = 1.65, WheelRadius = 0.33, CamDistance = 7, Eye = new(-0.4f, 0.95f, 0.2f) },
        new() { Id = "suv", Name = "4×4 (SUV)", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Car, Paint = Pick(Black, Silver, White, Green, Blue),
                Mass = 1900, TopSpeed = 53, Accel = 3.8, Brake = 9, Reverse = 8, Grip = 1.0, SlopeFactor = 0.45, Gears = 6,
                Length = 4.7, Width = 1.95, Height = 1.8, Wheelbase = 2.8, Track = 1.65, WheelRadius = 0.4, CamDistance = 8, Eye = new(-0.4f, 1.45f, 0.2f) },
        new() { Id = "pickup", Name = "Pick-up", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Truck,
                Mass = 2100, TopSpeed = 48, Accel = 3.6, Brake = 8.5, Reverse = 8, Grip = 1.0, SlopeFactor = 0.45, Gears = 6,
                Length = 4.9, Width = 2.0, Height = 1.9, Wheelbase = 3.0, Track = 1.7, WheelRadius = 0.5, CamDistance = 8.5, Eye = new(-0.42f, 1.6f, 0.4f) },
        new() { Id = "van", Name = "Camionnette", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Truck, Paint = Pick(White, Yellow, Silver, Blue),
                Mass = 2400, TopSpeed = 42, Accel = 2.6, Brake = 8, Reverse = 7, Grip = 0.95, Gears = 6,
                Length = 5.3, Width = 2.0, Height = 2.5, Wheelbase = 3.4, Track = 1.7, WheelRadius = 0.36, CamDistance = 9.5, Eye = new(-0.45f, 1.75f, 1.6f) },
        new() { Id = "bus", Name = "Bus", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Truck, Paint = Pick(Yellow, Blue, Red, White),
                Mass = 12000, TopSpeed = 28, Accel = 1.4, Brake = 6, Reverse = 5, Grip = 0.85, MaxSteer = 0.65, Gears = 6,
                Length = 12, Width = 2.55, Height = 3.1, Wheelbase = 6.2, Track = 2.1, WheelRadius = 0.5, CamDistance = 16, CamHeight = 2.4, Eye = new(-0.7f, 2.3f, 5.3f) },
        new() { Id = "truck", Name = "Camion", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Truck, Paint = Pick(Red, Blue, White, Orange, Green),
                Mass = 16000, TopSpeed = 25, Accel = 1.2, Brake = 5.5, Reverse = 5, Grip = 0.8, MaxSteer = 0.6, Gears = 8,
                Length = 10, Width = 2.5, Height = 3.6, Wheelbase = 6.0, Track = 2.0, WheelRadius = 0.52, CamDistance = 15, CamHeight = 2.6, Eye = new(-0.6f, 2.45f, 4.1f) },
        new() { Id = "tractor", Name = "Tracteur", Category = "Route", Family = VehicleFamily.Road, Sound = EngineSound.Truck, Paint = Pick(Green, Red, Blue),
                Mass = 4500, TopSpeed = 11, Accel = 1.8, Brake = 6, Reverse = 4, Grip = 0.9, SlopeFactor = 0.3, MaxSteer = 0.7, Gears = 4,
                Length = 4.2, Width = 2.2, Height = 2.9, Wheelbase = 2.4, Track = 1.7, WheelRadius = 0.8, CamDistance = 8, CamHeight = 2, Eye = new(0, 2.5f, -0.3f) },

        // --- Deux-roues
        new() { Id = "bike", Name = "Vélo", Category = "Deux-roues", Family = VehicleFamily.Bicycle, Sound = EngineSound.Bicycle, Paint = Pick(Teal, Red, Black, Yellow),
                Mass = 85, TopSpeed = 9, Accel = 1.6, Brake = 5.5, Reverse = 1.5, Grip = 0.9, SlopeFactor = 0.8, MaxSteer = 0.6, Gears = 1,
                Length = 1.75, Width = 0.6, Height = 1.8, Wheelbase = 1.05, Track = 0.1, WheelRadius = 0.34, CamDistance = 4.5, CamHeight = 1.4, Eye = new(0, 1.65f, -0.15f) },
        new() { Id = "moto", Name = "Moto", Category = "Deux-roues", Family = VehicleFamily.Motorbike, Sound = EngineSound.Motorbike,
                Mass = 260, TopSpeed = 62, Accel = 6.5, Brake = 9.5, Reverse = 1.5, Grip = 1.15, MaxSteer = 0.5, Gears = 6,
                Length = 2.2, Width = 0.8, Height = 1.4, Wheelbase = 1.45, Track = 0.1, WheelRadius = 0.39, CamDistance = 5.5, CamHeight = 1.3, Eye = new(0, 1.55f, 0.05f) },

        // --- Sur l'eau
        new() { Id = "boat", Name = "Bateau à moteur", Category = "Sur l'eau", Family = VehicleFamily.Boat, Sound = EngineSound.Outboard, Paint = Pick(White, Blue, Red),
                Mass = 1400, TopSpeed = 18, Accel = 2.2, Brake = 1.5, Reverse = 4, TurnRate = 0.5, Draft = 0.35,
                Length = 6, Width = 2.3, Height = 1.6, CamDistance = 11, CamHeight = 1.6, Eye = new(-0.45f, 1.7f, 0.5f) },
        new() { Id = "jetski", Name = "Jet-ski", Category = "Sur l'eau", Family = VehicleFamily.Boat, Sound = EngineSound.Outboard, Paint = Pick(Yellow, Red, Teal),
                Mass = 380, TopSpeed = 22, Accel = 5, Brake = 2.5, Reverse = 3, TurnRate = 1.0, Draft = 0.2,
                Length = 3.2, Width = 1.15, Height = 1.5, CamDistance = 6, CamHeight = 1.3, Eye = new(0, 1.5f, 0.2f) },
        new() { Id = "sailboat", Name = "Voilier", Category = "Sur l'eau", Family = VehicleFamily.Sailboat, Sound = EngineSound.Sail, Paint = Pick(White, Blue),
                Mass = 3500, TopSpeed = 8, Accel = 0.8, Brake = 0.8, Reverse = 2, TurnRate = 0.35, Draft = 0.5,
                Length = 9, Width = 3.0, Height = 13, CamDistance = 20, CamHeight = 3.5, CamPitch = -0.16, Eye = new(0, 1.9f, 3.2f) },

        // --- Dans les airs
        new() { Id = "plane", Name = "Avion de tourisme", Category = "Dans les airs", Family = VehicleFamily.Plane, Sound = EngineSound.Propeller, Paint = Pick(Red, Blue, Yellow),
                Mass = 1100, TopSpeed = 66, Accel = 3.2, Brake = 4, StallSpeed = 25, PitchRate = 0.65, RollRate = 1.4,
                Length = 8.3, Width = 11, Height = 2.8, CamDistance = 17, CamHeight = 1.9, CamPitch = -0.18, Eye = new(-0.3f, 2.3f, 0.6f) },
        new() { Id = "airliner", Name = "Avion de ligne", Category = "Dans les airs", Family = VehicleFamily.Jet, Sound = EngineSound.Jet, Paint = Pick(Blue, Red, Green),
                Mass = 70000, TopSpeed = 245, Accel = 2.6, Brake = 3, StallSpeed = 62, PitchRate = 0.3, RollRate = 0.55,
                Length = 38, Width = 35, Height = 12, CamDistance = 70, CamHeight = 6, CamPitch = -0.14, Eye = new(-0.5f, 5.1f, 16.2f) },
        new() { Id = "heli", Name = "Hélicoptère", Category = "Dans les airs", Family = VehicleFamily.Helicopter, Sound = EngineSound.Rotor, Paint = Pick(Red, Yellow, Blue, White),
                Mass = 2400, TopSpeed = 70, Accel = 4, TurnRate = 1.1, ClimbRate = 9,
                Length = 12, Width = 10.5, Height = 3.4, CamDistance = 17, CamHeight = 2, CamPitch = -0.2, Eye = new(-0.35f, 1.75f, 1.55f) },
    };

    public static VehicleSpec Get(string id) => All.Find(s => s.Id == id) ?? All[0];
}
