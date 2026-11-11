using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Sons du véhicule conduit. Échantillons CC0 de Kenney (Starter Kit Racing) : moteur en boucle dont la hauteur
/// et le volume suivent le régime et les gaz, crissement des pneus selon le dérapage, choc. Le reste est synthétisé
/// en direct (aucun fichier) : souffle du vent selon la vitesse, clapotis et sillage des bateaux, bourdonnement de
/// l'hélice, sifflement du réacteur, battement des pales du rotor, roue libre du vélo, alarme de décrochage.
/// </summary>
public partial class VehicleAudio : Node
{
    const string Dir = "res://assets/vehicles/audio/";
    const float MixRate = 22050;

    AudioStreamPlayer _engine, _skid, _impact, _synth;
    AudioStreamGeneratorPlayback _gen;
    AudioStream _car, _moto;
    Vehicle _current;
    readonly Random _rng = new(7);

    // état de la synthèse (lissé pour éviter les clics)
    double _phaseProp, _phaseJet, _phaseRotor, _phaseTick, _phaseHorn;
    float _lp1, _lp2, _lp3;
    float _wind, _water, _prop, _jet, _rotor, _tick, _horn;
    float _propHz = 60, _jetHz = 900, _rotorHz = 5;

    /// <summary>Volume général des véhicules (0..1).</summary>
    public static float Volume = 0.8f;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Pausable;
        _car = Load("engine.ogg");
        _moto = Load("engine-motorcycle.ogg");
        _engine = Player(null);
        _skid = Player(Load("skid.ogg"));
        _impact = Player(Load("impact.ogg"));
        var gen = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.3f };
        _synth = Player(gen);
    }

    static AudioStream Load(string file)
    {
        var s = GD.Load<AudioStream>(Dir + file);
        if (s is AudioStreamOggVorbis ogg && file != "impact.ogg") ogg.Loop = true;
        return s;
    }

    AudioStreamPlayer Player(AudioStream s)
    {
        var p = new AudioStreamPlayer { Stream = s, VolumeDb = -80 };
        AddChild(p);
        return p;
    }

    static float Db(float lin) => lin <= 0.0005f ? -80 : Mathf.LinearToDb(lin);

    /// <summary>Appelé chaque image avec le véhicule conduit (ou null).</summary>
    public void Update(Vehicle v, double delta)
    {
        if (v != _current)
        {
            _current = v;
            _engine.Stop(); _skid.Stop(); _synth.Stop(); _gen = null;
            if (v == null) return;
            _engine.Stream = v.Spec.Sound switch
            {
                EngineSound.Car or EngineSound.Truck => _car,
                EngineSound.Motorbike or EngineSound.Outboard => _moto,
                _ => null,
            };
            if (_engine.Stream != null) _engine.Play();
            _skid.Play();
            _synth.Play();
            _gen = _synth.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        }
        if (v == null) return;

        float vol = Volume;
        float rpm = (float)Math.Clamp(v.Rpm01, 0, 1);
        float thr = (float)Math.Clamp(Math.Abs(v.Throttle01), 0, 1);
        float speed = (float)Math.Abs(v.Speed);

        // Moteur échantillonné : hauteur selon le régime (camion plus grave, hors-bord plus aigu)
        if (_engine.Stream != null)
        {
            var (lo, hi, gain) = v.Spec.Sound switch
            {
                EngineSound.Truck => (0.45f, 1.15f, 0.9f),
                EngineSound.Motorbike => (0.6f, 1.9f, 0.85f),
                EngineSound.Outboard => (0.7f, 1.7f, 0.7f),
                _ => (0.55f, 1.7f, 0.8f),
            };
            if (v.Spec.Id == "sport") (lo, hi) = (0.6f, 2.1f);
            _engine.PitchScale = Mathf.Lerp(lo, hi, rpm);
            _engine.VolumeDb = Db(vol * gain * (0.35f + 0.45f * thr + 0.2f * rpm));
        }
        _skid.VolumeDb = Db(vol * 0.9f * (float)Math.Clamp(v.Slip, 0, 1) * Math.Clamp(speed / 8, 0, 1));
        _skid.PitchScale = 0.9f + 0.2f * (float)v.Slip;
        if (v.Impact > 0.05)
        {
            _impact.VolumeDb = Db(vol * (float)Math.Clamp(v.Impact, 0.2, 1));
            _impact.PitchScale = 1.15f - 0.35f * (float)v.Impact;
            _impact.Play();
            v.Impact = 0;
        }

        // Cibles de la synthèse (lissées dans Fill)
        var snd = v.Spec.Sound;
        bool inAir = v.Spec.InAir && !v.Grounded;
        _wind = Mathf.Clamp(speed / (inAir ? 90 : 45), 0, 1) * (v.FirstPerson && !v.Spec.TwoWheels && v.Spec.Family != VehicleFamily.Bicycle ? 0.35f : 0.8f);
        if (v.Spec.TwoWheels) _wind *= 1.2f;
        _water = v.Spec.OnWater ? Mathf.Clamp(0.15f + speed / 15, 0, 1) : 0;
        _prop = snd == EngineSound.Propeller ? 0.25f + 0.55f * rpm : 0;
        _propHz = 35 + 85 * rpm;
        _jet = snd == EngineSound.Jet ? 0.15f + 0.6f * rpm : 0;
        _jetHz = 700 + 1800 * rpm;
        _rotor = snd == EngineSound.Rotor ? (float)Math.Clamp(v.Rpm01 - 0.15, 0, 1) : 0;
        _rotorHz = 1.5f + 4.5f * rpm;
        _tick = snd == EngineSound.Bicycle && thr <= 0 && speed > 1 ? Mathf.Clamp(speed / 6, 0, 1) : 0;
        _horn = v is Plane p && p.Stalling ? 1 : 0;
        Fill(speed);
    }

    float Noise() => (float)(_rng.NextDouble() * 2 - 1);

    float _sWind, _sWater, _sProp, _sJet, _sRotor, _sTick, _sHorn;

    void Fill(float speed)
    {
        if (_gen == null) return;
        int n = _gen.GetFramesAvailable();
        if (n <= 0) return;
        var buf = new Vector2[n];
        float vol = Volume;
        float tickHz = Math.Max(2, speed * 2.2f);
        for (int i = 0; i < n; i++)
        {
            const float k = 0.0008f;
            _sWind += (_wind - _sWind) * k; _sWater += (_water - _sWater) * k; _sProp += (_prop - _sProp) * k;
            _sJet += (_jet - _sJet) * k; _sRotor += (_rotor - _sRotor) * k; _sTick += (_tick - _sTick) * k; _sHorn += (_horn - _sHorn) * k * 4;

            float nz = Noise();
            // vent : bruit filtré (passe-bas qui s'ouvre avec la vitesse)
            float a1 = 0.02f + 0.12f * _sWind;
            _lp1 += (nz - _lp1) * a1;
            float s = _lp1 * _sWind * 0.9f;
            // eau : bruit plus clair, modulé (clapotis)
            _lp2 += (Noise() - _lp2) * 0.25f;
            s += (_lp2 - _lp1 * 0.5f) * _sWater * 0.35f * (0.75f + 0.25f * (float)Math.Sin(_phaseTick * 0.37));
            // hélice : dent de scie grave, adoucie
            _phaseProp += _propHz / MixRate;
            float saw = (float)(_phaseProp % 1) * 2 - 1;
            _lp3 += (saw - _lp3) * 0.15f;
            s += _lp3 * _sProp * 0.45f;
            // réacteur : sifflement + grondement
            _phaseJet += _jetHz / MixRate;
            s += ((float)Math.Sin(_phaseJet * Mathf.Tau) * 0.25f + _lp1 * 1.2f) * _sJet * 0.5f;
            // rotor : impulsions de bruit grave à la fréquence des pales (« flap flap »)
            _phaseRotor += _rotorHz / MixRate;
            float ph = (float)(_phaseRotor % 1);
            float env = ph < 0.18f ? (float)Math.Sin(ph / 0.18f * Math.PI) : 0;
            s += (_lp1 * 2.2f * env + (float)Math.Sin(_phaseRotor * Mathf.Tau * 6) * 0.08f) * _sRotor * 0.7f;
            // roue libre du vélo : petits cliquetis
            _phaseTick += tickHz / MixRate;
            float tp = (float)(_phaseTick % 1);
            s += (tp < 0.04f ? nz * (1 - tp / 0.04f) : 0) * _sTick * 0.35f;
            // alarme de décrochage : bip aigu intermittent
            _phaseHorn += 1.0 / MixRate;
            if ((_phaseHorn % 0.5) < 0.3) s += (float)Math.Sin(_phaseHorn * 2800 * Mathf.Tau) * _sHorn * 0.18f;

            s = Mathf.Clamp(s * vol, -1, 1);
            buf[i] = new Vector2(s, s);
        }
        _gen.PushBuffer(buf);
    }
}
