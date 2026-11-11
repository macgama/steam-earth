using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Shader des façades et des toits (bâtiments Overture et modèles importés). Les matières (<see cref="Surface"/>) sont
/// dessinées sans aucune image, d'après les UV en mètres : crépi, madriers de chalet, pierre, brique, béton banché,
/// bardage, colombages, clins ; tuiles, ardoise ou fibrociment, tôle, tavillons, gravier, cuivre, toit végétalisé,
/// lauzes ; balustrades de balcon. Les fenêtres d'habitation reçoivent un cadre, une tablette et parfois des volets.
/// Une variante par niveau de qualité (QualityProfile.BuildingDetail) :
/// 0 Bas = motifs simples sans bruit ni relief ; 1 Moyen = bruit léger, cadres de fenêtres, volets ;
/// 2 Élevé = relief des joints (normale perturbée), salissures au pied des murs ; 3 Ultra = relief plus fin
/// (différences finies), coulures de pluie, mousse sur les toits, un octave de bruit de plus.
/// </summary>
public static class BuildingShader
{
    static readonly Dictionary<int, Shader> Cache = new();

    public static Shader Get(int detail)
    {
        detail = Mathf.Clamp(detail, 0, 3);
        if (!Cache.TryGetValue(detail, out var s)) Cache[detail] = s = new Shader { Code = Code(detail) };
        return s;
    }

    public static string Code(int detail) => Head + $"#define DETAIL {detail}\n" + Body;

    const string Head = @"shader_type spatial;
render_mode cull_disabled;
";

    // Fenêtres dessinées selon (abscisse le long du mur, étage), allumées la nuit. UV2.x = motif (WindowStyle) :
    // fenêtres d'habitation, bandeaux de bureaux, vitraux d'église, vitrines, baies d'école, mur-rideau, porte (effacée
    // près du joueur), gradins, pelouse, auvent rayé. Vues de l'intérieur (face arrière), les vitres laissent voir le jour.
    // Alpha de la couleur des sommets = 1 − code / 255 : matière (bits 0–5) et volets (bit 6).
    const string Body = @"
#if DETAIL >= 3
#define OCT 4
#elif DETAIL == 2
#define OCT 3
#else
#define OCT 2
#endif
uniform float night = 0.0;
uniform float lit_fraction = 0.5;
uniform float shop_lit = 1.0;
uniform vec3 player_world = vec3(0.0, -1e9, 0.0);
uniform float door_open = 7.0;
varying vec3 wpos;
void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
// hachage sans sinus (précis même avec de grandes coordonnées : pas de grain dans les fenêtres)
float hash(vec3 p) {
    p = fract(mod(p, 4096.0) * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}
float box(vec2 f, float x0, float x1, float y0, float y1) {
    return step(x0, f.x) * step(f.x, x1) * step(y0, f.y) * step(f.y, y1);
}
#if DETAIL >= 1
float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash(vec3(i, 7.0));
    float b = hash(vec3(i + vec2(1.0, 0.0), 7.0));
    float c = hash(vec3(i + vec2(0.0, 1.0), 7.0));
    float d = hash(vec3(i + vec2(1.0, 1.0), 7.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float fbm(vec2 p) {
    float s = 0.0;
    float a = 0.5;
    float t = 0.0;
    for (int i = 0; i < OCT; i++) { s += a * vnoise(p); t += a; p = p * 2.07 + vec2(13.7, 7.3); a *= 0.5; }
    return s / t;
}
#else
float vnoise(vec2 p) { return 0.5; }
float fbm(vec2 p) { return 0.5; }
#endif
// 1 de près, 0 quand le motif devient plus fin qu'un pixel (on garde alors la teinte moyenne : pas de moiré)
float aa(float period, float fw) {
    return 1.0 - smoothstep(0.12 * period, 0.5 * period, fw);
}
vec3 lin(vec3 c) { return pow(c, vec3(2.2)); }

// Aspect d'une matière en (u, v) mètres : couleur linéaire, relief (m), rugosité, métal, découpe (balustrades).
void material(int mat, vec2 uv, float seed, vec3 base, float fw,
              out vec3 alb, out float h, out float rough, out float metal, out float cut) {
    alb = base; h = 0.0; rough = 0.9; metal = 0.0; cut = 0.0;
    float sd = floor(seed * 997.0);
    int m = mat;
    vec3 b = base;
    // chalet : soubassement et rez-de-chaussée maçonnés et crépis, étages en bois (couleur des sommets = bois)
    if (m == 7) {
        if (uv.y < 3.0) { m = 1; b = lin(vec3(0.86, 0.84, 0.78)); } else { m = 2; }
    }
    if (m == 1) {
        // crépi : teinte qui varie doucement, grain fin
        alb = b * (0.93 + 0.12 * fbm(uv * 3.1 + sd));
        h = 0.004 * vnoise(uv * 22.0) * aa(0.05, fw);
        rough = 0.95;
    } else if (m == 2 || m == 9) {
        // planches horizontales : madriers de 20 cm (bois brut) ou clins peints de 15 cm qui se recouvrent
        float bh = m == 9 ? 0.15 : 0.2;
        float row = floor(uv.y / bh);
        float fy = fract(uv.y / bh);
        float rv = hash(vec3(row, sd, 2.0));
        float k = aa(bh, fw);
        float v;
        if (m == 9) {
            v = mix(0.70, 1.0, smoothstep(0.0, 0.35, fy)) * (0.96 + 0.06 * rv);
            h = fy * 0.012 * k;
            rough = 0.7;
        } else {
            float grain = 0.86 + 0.24 * vnoise(vec2(uv.x * 0.8 + rv * 40.0, uv.y * 26.0));
            float gap = 1.0 - 0.6 * (1.0 - smoothstep(0.02, 0.08, fy));
            float endj = 1.0 - 0.5 * (1.0 - smoothstep(0.0, 0.012, fract((uv.x + rv * 7.0) / 4.6) * 4.6));
            v = (0.80 + 0.32 * rv) * grain * gap * endj;
            h = smoothstep(0.02, 0.10, fy) * (1.0 - smoothstep(0.90, 0.99, fy)) * 0.012 * k;
            rough = 0.85;
        }
        alb = b * mix(0.92, v, k);
    } else if (m == 3 || m == 23) {
        // moellons (murs) ou lauzes (toits) : rangées irrégulières, joints de mortier ou recouvrement
        bool roof = m == 23;
        float rh = roof ? 0.42 : 0.32;
        float row = floor(uv.y / rh);
        float fy = fract(uv.y / rh);
        float rr = hash(vec3(row, sd, 3.0));
        float bw = (roof ? 0.45 : 0.5) + 0.35 * rr;
        float x = uv.x / bw + rr * 3.0;
        float fx = fract(x);
        float bv = hash(vec3(floor(x), row, sd + 5.0));
        float e = min(min(fx, 1.0 - fx) * bw, min(fy, 1.0 - fy) * rh);
        float k = aa(rh, fw);
        vec3 stone = b * (0.72 + 0.45 * bv) * (0.88 + 0.24 * fbm(uv * 4.0 + bv * 9.0));
        if (roof) {
            float shade = mix(0.55, 1.0, smoothstep(0.0, 0.18, fy));
            alb = mix(b * 0.88, stone * shade, k);
            h = (fy * 0.04 + 0.01 * smoothstep(0.0, 0.03, e)) * k;
#if DETAIL >= 2
            // lichens jaunes
            alb = mix(alb, lin(vec3(0.55, 0.53, 0.36)), smoothstep(0.68, 0.85, vnoise(uv * 3.0 + 50.0)) * 0.35 * k);
#endif
        } else {
            float mortar = 1.0 - smoothstep(0.008, 0.024, e);
            alb = mix(b * 0.95, mix(stone, b * 0.8 + lin(vec3(0.35)), mortar), k);
            h = (smoothstep(0.0, 0.05, e) * 0.02 + 0.006 * fbm(uv * 9.0)) * k;
        }
        rough = 0.92;
    } else if (m == 4) {
        // brique : appareil en panneresses, joints clairs
        float rh = 0.085;
        float bl = 0.26;
        float row = floor(uv.y / rh);
        float fy = fract(uv.y / rh);
        float x = uv.x / bl + 0.5 * mod(row, 2.0);
        float fx = fract(x);
        float bv = hash(vec3(floor(x), row, sd + 9.0));
        float e = min(min(fx, 1.0 - fx) * bl, min(fy, 1.0 - fy) * rh);
        float mortar = 1.0 - smoothstep(0.004, 0.009, e);
        float k = aa(rh, fw);
        vec3 mort = lin(vec3(0.68, 0.66, 0.62));
        alb = mix(mix(b, mort, 0.16), mix(b * (0.78 + 0.4 * bv), mort, mortar), k);
        h = smoothstep(0.0, 0.012, e) * 0.008 * k;
        rough = 0.9;
    } else if (m == 5) {
        // béton banché : panneaux de coffrage et trous de banches
        vec2 pnl = uv / vec2(2.4, 1.2);
        vec2 f = fract(pnl);
        float pv = hash(vec3(floor(pnl), sd + 11.0));
        float e = min(min(f.x, 1.0 - f.x) * 2.4, min(f.y, 1.0 - f.y) * 1.2);
        float joint = 1.0 - smoothstep(0.004, 0.012, e);
        vec2 tie = (fract(uv / 0.6) - 0.5) * 0.6;
        float hole = 1.0 - smoothstep(0.012, 0.02, length(tie));
        float k = aa(0.6, fw);
        alb = b * (0.92 + 0.1 * pv) * (0.9 + 0.14 * fbm(uv * 2.3 + pv)) * (1.0 - (0.3 * joint + 0.4 * hole) * k);
        h = -(joint * 0.004 + hole * 0.008) * k;
        rough = 0.9;
    } else if (m == 6) {
        // bardage métallique ondulé (nervures verticales tous les 20 cm)
        float s = sin(fract(uv.x / 0.2) * 6.2832);
        float k = aa(0.2, fw);
        alb = b * (0.93 + 0.09 * s * k);
        h = s * 0.012 * k;
        rough = 0.5;
        metal = 0.45;
    } else if (m == 8) {
        // colombages : poteaux, sablières, écharpes en bois sombre dans le crépi
        vec3 beam = lin(vec3(0.27, 0.17, 0.10));
        float ly = mod(uv.y, 3.0);
        float px = mod(uv.x, 1.1);
        float post = 1.0 - step(0.16, px);
        float rail = step(ly, 0.20) + step(2.82, ly) + step(abs(ly - 1.0), 0.07);
        float dg = hash(vec3(floor(uv.x / 1.1), floor(uv.y / 3.0), sd + 13.0));
        float dx = dg < 0.2 ? 1.1 - px : px;
        float diag = dg < 0.45 ? step(abs(dx - ly * 0.3667), 0.09) : 0.0;
        float k = aa(0.4, fw) * step(0.0, uv.y);
        float w = clamp(post + rail + diag, 0.0, 1.0) * k;
        alb = mix(b * (0.93 + 0.12 * fbm(uv * 3.0)), beam * (0.8 + 0.4 * vnoise(vec2(uv.x * 2.0, uv.y * 9.0))), w);
        h = w * 0.015;
        rough = 0.93;
    } else if (m == 16) {
        float k;
        if (hash(vec3(sd, 16.0, 1.0)) < 0.55) {
            // tuiles mécaniques à côtes
            float rh = 0.34;
            float tw = 0.21;
            float row = floor(uv.y / rh);
            float fy = fract(uv.y / rh);
            float x = uv.x / tw;
            float tv = hash(vec3(floor(x), row, sd + 17.0));
            float wave = 0.5 + 0.5 * cos(fract(x) * 6.2832);
            float overlap = smoothstep(0.0, 0.22, fy);
            k = aa(tw, fw);
            alb = b * mix(0.9, (0.80 + 0.32 * tv) * (0.72 + 0.28 * overlap) * (0.9 + 0.12 * wave), k);
            h = (fy * 0.025 + wave * 0.015) * k;
        } else {
            // tuiles plates en queue de castor, rangées décalées
            float rh = 0.15;
            float tw = 0.18;
            float row = floor(uv.y / rh);
            float fy = fract(uv.y / rh);
            float x = uv.x / tw + 0.5 * mod(row, 2.0);
            float fx = fract(x);
            float tv = hash(vec3(floor(x), row, sd + 19.0));
            float c = 2.0 * fx - 1.0;
            float edge = 1.0 - 0.45 * (1.0 - sqrt(max(1.0 - c * c, 0.0)));
            float under = step(edge, fy);
            float gap = 1.0 - smoothstep(0.0, 0.006, min(fx, 1.0 - fx) * tw);
            k = aa(rh, fw);
            alb = b * mix(0.88, (0.80 + 0.3 * tv) * mix(1.0, 0.62, under) * (1.0 - 0.5 * gap) * (0.8 + 0.2 * smoothstep(0.0, 0.25, fy)), k);
            h = (fy * 0.015 * (1.0 - under)) * k;
        }
        rough = 0.8;
    } else if (m == 17) {
        // ardoise ou fibrociment : plaques rectangulaires décalées, ou posées en losanges
        bool diamond = hash(vec3(sd, 17.0, 2.0)) < 0.35;
        vec2 q = diamond ? vec2(uv.x + uv.y, uv.y - uv.x) * 0.7071 : uv;
        float rh = diamond ? 0.3 : 0.2;
        float tw = diamond ? 0.3 : 0.36;
        float row = floor(q.y / rh);
        float fy = fract(q.y / rh);
        float x = q.x / tw + (diamond ? 0.0 : 0.5 * mod(row, 2.0));
        float fx = fract(x);
        float tv = hash(vec3(floor(x), row, sd + 23.0));
        float e = min(min(fx, 1.0 - fx) * tw, min(fy, 1.0 - fy) * rh);
        float joint = 1.0 - smoothstep(0.002, 0.007, e);
        float k = aa(rh, fw);
        alb = b * mix(0.92, (0.82 + 0.3 * tv) * (1.0 - 0.45 * joint) * (0.78 + 0.22 * smoothstep(0.0, 0.15, fy)), k);
        h = (fy * 0.008 - joint * 0.003) * k;
        rough = 0.6;
    } else if (m == 18 || m == 21) {
        // tôle ou cuivre à joints debout (dans le sens de la pente, tous les 50 cm)
        float fx = fract(uv.x / 0.5);
        float e = min(fx, 1.0 - fx) * 0.5;
        float seam = 1.0 - smoothstep(0.006, 0.014, e);
        float k = aa(0.5, fw);
        float streak = fbm(vec2(uv.x * 0.6, uv.y * 0.08) + sd);
        if (m == 21) {
            alb = mix(b, lin(vec3(0.45, 0.30, 0.20)), smoothstep(0.55, 0.8, streak) * 0.6) * (1.0 + 0.12 * seam * k);
            rough = 0.6;
            metal = 0.25;
        } else {
            alb = b * (1.0 + 0.15 * seam * k) * (0.94 + 0.1 * streak);
            rough = 0.42;
            metal = 0.55;
        }
        h = seam * 0.02 * k;
    } else if (m == 19) {
        // tavillons (bardeaux de bois fendus) ou bardeaux d'asphalte, petites rangées décalées
        float rh = 0.11;
        float row = floor(uv.y / rh);
        float fy = fract(uv.y / rh);
        float rr = hash(vec3(row, sd, 19.0));
        float tw = 0.08 + 0.05 * rr;
        float x = uv.x / tw + rr * 5.0;
        float fx = fract(x);
        float tv = hash(vec3(floor(x), row, sd + 29.0));
        float gap = 1.0 - smoothstep(0.0, 0.005, min(fx, 1.0 - fx) * tw);
        float k = aa(rh, fw);
        float grain = 0.88 + 0.24 * vnoise(vec2(uv.x * 45.0, uv.y * 3.0));
        alb = b * mix(0.9, (0.78 + 0.36 * tv) * grain * (1.0 - 0.45 * gap) * (0.7 + 0.3 * smoothstep(0.0, 0.3, fy)), k);
        h = fy * 0.01 * k;
        rough = 0.95;
    } else if (m == 20) {
        // gravier
        float g = vnoise(uv * 35.0);
        float k = aa(0.06, fw);
        alb = b * (0.9 + 0.25 * (g - 0.5) * k) * (0.88 + 0.24 * fbm(uv * 0.8 + sd));
        h = g * 0.006 * k;
        rough = 1.0;
    } else if (m == 22) {
        // toit végétalisé : sedum vert et taches rousses
        float g = fbm(uv * 1.3 + sd);
        alb = mix(b, b * vec3(1.6, 1.0, 0.45), smoothstep(0.5, 0.75, g)) * (0.85 + 0.3 * vnoise(uv * 12.0));
        h = vnoise(uv * 12.0) * 0.01 * aa(0.1, fw);
        rough = 1.0;
    } else if (m == 30) {
        // balustrade de chalet : planches jointives découpées d'un motif, main courante et lisse basse
        float bw = 0.12;
        float fx = fract(uv.x / bw);
        float bv = hash(vec3(floor(uv.x / bw), sd, 30.0));
        float rails = step(0.90, uv.y) + step(uv.y, 0.10);
        float k = aa(bw, fw);
        float hole = (1.0 - rails) * (1.0 - step(0.035, length(vec2((fx - 0.5) * bw, (uv.y - 0.58) * 0.8))));
        float slot = (1.0 - rails) * step(abs(fx - 0.5) * bw, 0.012) * step(abs(uv.y - 0.36), 0.12);
        float gap = (1.0 - rails) * (1.0 - step(0.012 / bw, min(fx, 1.0 - fx) * 2.0));
        // au loin : part de vide moyenne (≈ 15 %), tramée par le fragment()
        cut = mix(0.15, max(max(hole, slot), gap), k);
        alb = b * mix(0.92, (0.82 + 0.3 * bv) * (0.9 + 0.2 * vnoise(vec2(uv.x * 30.0, uv.y * 3.0))), k);
        rough = 0.85;
    } else if (m == 31) {
        // garde-corps à barreaux
        float fx = fract(uv.x / 0.11);
        float bar = step(min(fx, 1.0 - fx) * 0.11, 0.009);
        float rails = step(0.94, uv.y) + step(uv.y, 0.05);
        float k = aa(0.11, fw);
        cut = mix(0.7, 1.0 - clamp(bar + rails, 0.0, 1.0), k);
        rough = 0.4;
        metal = 0.6;
    } else if (m == 32) {
        // plancher de balcon
        float row = floor(uv.y / 0.12);
        float fy = fract(uv.y / 0.12);
        float k = aa(0.12, fw);
        float rv = hash(vec3(row, sd, 32.0));
        alb = b * mix(0.92, (0.82 + 0.3 * rv) * (1.0 - 0.5 * (1.0 - smoothstep(0.0, 0.08, fy))), k);
        rough = 0.85;
    }
#if DETAIL >= 2
    // murs : plus sombres au ras du sol (éclaboussures, humidité)
    if (m >= 1 && m <= 9) alb *= mix(0.80, 1.0, smoothstep(-0.4, 0.7, uv.y));
#endif
#if DETAIL >= 3
    if (m >= 1 && m <= 9) {
        // coulures de pluie verticales
        alb *= 1.0 - 0.08 * smoothstep(0.6, 0.9, vnoise(vec2(uv.x * 7.0, uv.y * 0.25) + sd));
    } else if (m >= 16 && m <= 19) {
        // mousse et salissures sur les pans
        alb = mix(alb, alb * vec3(0.78, 0.88, 0.62), smoothstep(0.58, 0.85, fbm(uv * 0.7 + sd)) * 0.55);
    }
#endif
}

float relief(int mat, vec2 uv, float seed, vec3 base, float fw) {
    vec3 a; float h; float r; float mt; float c;
    material(mat, uv, seed, base, fw, a, h, r, mt, c);
    return h;
}

vec3 shutter_color(float seed) {
    float r = hash(vec3(floor(seed * 997.0), 5.0, 61.0));
    vec3 c = r < 0.30 ? vec3(0.20, 0.34, 0.22) : r < 0.47 ? vec3(0.50, 0.13, 0.10) : r < 0.61 ? vec3(0.55, 0.57, 0.56)
        : r < 0.76 ? vec3(0.86, 0.86, 0.83) : r < 0.90 ? vec3(0.40, 0.26, 0.15) : vec3(0.30, 0.40, 0.48);
    return lin(c);
}

void fragment() {
    int code = int(round((1.0 - COLOR.a) * 255.0));
    int mat = code & 63;
    bool shutters = (code & 64) != 0;
    float fw = max(length(fwidth(UV)), 1e-5);
    vec3 base;
    float h;
    float rough;
    float metal;
    float cut;
    material(mat, UV, UV2.y, pow(COLOR.rgb, vec3(2.2)), fw, base, h, rough, metal, cut);
    // balustrades : découpes nettes de près ; au loin, vide tramé (pas de moiré, pas de panneau plein)
    if (cut > 0.0 && cut >= hash(vec3(FRAGCOORD.xy, 3.0)) * 0.98 + 0.01) discard;
    ROUGHNESS = rough;
    METALLIC = metal;
    SPECULAR = 0.25;
    int style = int(UV2.x + 0.5);
    float seed = UV2.y;
    if (style == 0) { ALBEDO = base; }
    else if (style == 8) {
        // porte d'entrée : effacée quand le joueur s'approche (il voit l'intérieur, la porte ouverte)
        if (distance(wpos, player_world) < door_open) discard;
        float panel = step(0.5, fract(UV.y / 0.5)) * 0.12;
        float glass = step(1.45, UV.y) * step(UV.y, 1.95) * step(0.12, fract(UV.x / 0.55));
        ALBEDO = mix(base * (0.9 + panel), vec3(0.06, 0.07, 0.09), glass);
        ROUGHNESS = mix(0.6, 0.2, glass);
    } else if (style == 10) {
        // gradins : rangées de sièges séparées par des marches de béton
        float row = fract(UV.y / 0.45);
        ALBEDO = mix(base, vec3(0.45), step(0.72, row));
    } else if (style == 11) {
        // pelouse tondue en bandes
        float band = step(0.5, fract(UV.x / 5.5));
        ALBEDO = base * mix(0.85, 1.08, band);
        ROUGHNESS = 1.0;
    } else if (style == 13) {
        // toile d'auvent rayée
        ALBEDO = mix(base, vec3(0.85, 0.83, 0.78), step(0.5, fract(UV.x / 0.45)));
        ROUGHNESS = 1.0;
    } else {
        float cw = style == 3 ? 4.0 : 3.2;
        vec2 cell = vec2(UV.x / cw, UV.y / 3.0);
        vec2 f = fract(cell);
        float lvl = floor(cell.y);
        float above = step(0.0, UV.y);
        float win = 0.0;
        bool shop = false;
        if ((style == 4 || style == 9) && lvl < 0.5) {
            // vitrine au rez-de-chaussée
            win = box(f, 0.06, 0.94, 0.05, 0.80) * (1.0 - box(vec2(fract(UV.x / 1.6), f.y), 0.0, 0.025, 0.0, 1.0));
            shop = true;
        } else if (style == 1 || style == 4) {
            win = box(f, 0.30, 0.72, 0.32, 0.78);
        } else if (style == 2 || style == 9) {
            win = box(f, 0.0, 1.0, 0.30, 0.86) * step(0.05, fract(UV.x / 1.6));
        } else if (style == 3) {
            // vitraux : hautes fenêtres à arc
            vec2 q = vec2((f.x - 0.5) / 0.13, (f.y - 0.62) / 0.12);
            win = max(box(f, 0.37, 0.63, 0.16, 0.62), step(length(q), 1.0) * step(0.62, f.y));
        } else if (style == 5) {
            win = box(f, 0.0, 1.0, 0.72, 0.88) * step(0.06, fract(UV.x / 1.2));
        } else if (style == 6) {
            win = box(f, 0.06, 0.94, 0.28, 0.86) * (1.0 - box(f, 0.49, 0.51, 0.0, 1.0));
        } else if (style == 7) {
            win = (1.0 - step(fract(UV.x / 1.5), 0.04)) * (1.0 - step(f.y, 0.07));
        } else if (style == 12) {
            win = box(f, 0.18, 0.82, 0.14, 0.88) * step(0.05, fract(UV.x / 0.8));
        }
        win *= above;
        // au loin, les fenêtres se fondent dans la façade (pas de scintillement)
        float blur = clamp(max(fwidth(cell.x), fwidth(cell.y)) * 2.5 - 0.5, 0.0, 1.0);
        // chaque fenêtre a sa valeur fixe : quand la part allumée baisse (nuit), ce sont toujours les mêmes qui s'éteignent
        float lit = step(1.0 - lit_fraction, hash(vec3(floor(cell), floor(seed * 997.0))));
        vec3 wallc = base;
#if DETAIL >= 1
        if ((style == 1 || style == 4) && !shop && above > 0.5) {
            float k = 1.0 - blur;
            // cadre blanc et tablette, vitre en retrait
            float frame = min(box(f, 0.285, 0.735, 0.30, 0.795) * (1.0 - win) + box(f, 0.27, 0.75, 0.285, 0.325), 1.0);
            wallc = mix(wallc, lin(vec3(0.80, 0.79, 0.76)), frame * k);
            h = mix(h, 0.015, frame * k);
            h = mix(h, -0.05, win * k);
            if (shutters) {
                // volets à lames, une couleur par maison ; certains fermés (davantage la nuit, jamais devant une fenêtre allumée)
                float wh = hash(vec3(floor(cell), floor(seed * 997.0) + 41.0));
                float closed = step(wh, mix(0.12, 0.5, night)) * (1.0 - lit * night);
                vec3 sc = shutter_color(seed) * mix(1.0, 0.82 + 0.18 * step(0.45, fract(UV.y / 0.07)), k);
                if (closed > 0.5) {
                    float leaf = win * (1.0 - box(f, 0.505, 0.515, 0.0, 1.0));
                    wallc = mix(wallc, sc, win * k);
                    wallc = mix(wallc, sc * 0.5, (win - leaf) * k);
                    h = mix(h, 0.01, win * k);
                    win = 0.0;
                } else {
                    float sh = box(f, 0.095, 0.295, 0.32, 0.78) + box(f, 0.725, 0.925, 0.32, 0.78);
                    wallc = mix(wallc, sc, sh * k);
                    h = mix(h, 0.02, sh * k);
                }
            }
        }
#endif
        float m = mix(win, style == 7 ? 0.8 : 0.2, blur);
        vec3 glass = vec3(0.05, 0.065, 0.08);
        if (style == 3) glass = mix(vec3(0.10, 0.12, 0.28), vec3(0.30, 0.10, 0.10), step(0.5, hash(vec3(floor(cell), 3.0))));
        if (shop) glass = vec3(0.16, 0.14, 0.11); // vitrine : on devine l'intérieur éclairé
        if (!FRONT_FACING) glass = mix(vec3(0.55, 0.68, 0.85), vec3(0.03, 0.04, 0.07), night); // vu de l'intérieur : le jour dehors
        ALBEDO = mix(wallc, glass, m);
        ROUGHNESS = mix(rough, 0.25, m);
        METALLIC = mix(metal, 0.0, m);
        vec3 warm = vec3(1.0, 0.70, 0.36);
        if (shop) {
            EMISSION = vec3(1.0, 0.86, 0.62) * mix(0.25, 1.2, night) * shop_lit * mix(win, 0.3, blur);
        } else if (style == 3) {
            EMISSION = vec3(0.9, 0.6, 0.35) * 0.35 * night * win * step(0.6, lit_fraction + 0.3);
        } else {
            EMISSION = warm * 0.9 * night * mix(win * lit, 0.2 * lit_fraction, blur);
        }
        if (!FRONT_FACING) EMISSION = vec3(0.55, 0.68, 0.85) * 0.6 * (1.0 - night) * win;
    }
#if DETAIL >= 2
    // relief des joints, des tuiles, des planches : normale perturbée
    vec3 dpx = dFdx(VERTEX);
    vec3 dpy = dFdy(VERTEX);
#if DETAIL >= 3
    // Ultra : pente du relief par différences finies dans le plan (u, v), sans les marches de 2×2 pixels
    vec2 dux = dFdx(UV);
    vec2 duy = dFdy(UV);
    vec3 t = cross(dpy, NORMAL) * dux.x + cross(NORMAL, dpx) * duy.x;
    vec3 bt = cross(dpy, NORMAL) * dux.y + cross(NORMAL, dpx) * duy.y;
    if (mat != 0 && dot(t, t) > 1e-24 && dot(bt, bt) > 1e-24 && style != 8) {
        float e = 0.01;
        vec3 bc = pow(COLOR.rgb, vec3(2.2));
        float hu = (relief(mat, UV + vec2(e, 0.0), UV2.y, bc, fw) - relief(mat, UV - vec2(e, 0.0), UV2.y, bc, fw)) / (2.0 * e);
        float hv = (relief(mat, UV + vec2(0.0, e), UV2.y, bc, fw) - relief(mat, UV - vec2(0.0, e), UV2.y, bc, fw)) / (2.0 * e);
        // le cadre et les volets (dessinés par-dessus la matière) gardent la méthode à l'écran
        float extra = h - relief(mat, UV, UV2.y, bc, fw);
        vec3 n = normalize(NORMAL - hu * normalize(t) - hv * normalize(bt));
        vec3 r1 = cross(dpy, n);
        vec3 r2 = cross(n, dpx);
        float det = dot(dpx, r1);
        vec3 grad = sign(det) * (dFdx(extra) * r1 + dFdy(extra) * r2);
        NORMAL = normalize(abs(det) * n - grad);
    } else
#endif
    {
        vec3 r1 = cross(dpy, NORMAL);
        vec3 r2 = cross(NORMAL, dpx);
        float det = dot(dpx, r1);
        vec3 grad = sign(det) * (dFdx(h) * r1 + dFdy(h) * r2);
        if (abs(det) > 1e-12) NORMAL = normalize(abs(det) * NORMAL - grad);
    }
#endif
    // vu de l'intérieur (face arrière) : le mur est éclairé par la pièce, pas par le soleil du dehors
    if (!FRONT_FACING) EMISSION += ALBEDO * mix(0.55, 0.40, night);
}";
}
