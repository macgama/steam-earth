// Surface de l'océan et des lacs : somme de vagues de Gerstner (houle réelle GFS-Wave + mer du vent + clapot).
// Repère : VERTEX est la position dans le repère planète, relative au centre du chunk.
// UV = (centre du chunk modulo PERIOD).xy, UV2 = ((centre modulo PERIOD).z, profondeur d'eau en m),
// COLOR.r = 1 en mer (0 sur un lac), COLOR.g = log2(espacement des sommets) codé.
// Les vecteurs d'onde sont des multiples de 2π/PERIOD sur chaque axe : la phase est donc identique d'un chunk
// à l'autre malgré le modulo, et reste précise en float partout sur la planète.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

const int MAX_WAVES = 64;
uniform int wave_count = 0;
uniform vec4 wave_k[MAX_WAVES];  // xyz : vecteur d'onde (rad/m, repère planète), w : amplitude (m)
uniform vec4 wave_p[MAX_WAVES];  // x : phase temporelle, y : raideur Q, z : longueur d'onde (m), w : facteur sur les lacs
uniform float whitecaps = 0.0;   // 0..1 : moutons selon le vent
uniform float time_s = 0.0;
uniform vec3 deep_color : source_color = vec3(0.012, 0.055, 0.12);
uniform vec3 shallow_color : source_color = vec3(0.03, 0.24, 0.26);
uniform vec3 lake_color : source_color = vec3(0.03, 0.11, 0.12);
uniform vec3 sand_color : source_color = vec3(0.62, 0.57, 0.43);
uniform float night = 0.0;

varying vec3 v_q;
varying vec3 v_up;
varying float v_depth;
varying float v_sea;
varying float v_height;

float shoal(float depth, float lam) {
	// les vagues s'écrasent en arrivant sur la plage
	return smoothstep(0.0, 1.5 + 0.04 * lam, depth);
}

void vertex() {
	vec3 up = normalize(NORMAL);
	vec3 q = vec3(UV.x, UV.y, UV2.x) + VERTEX;
	float depth = UV2.y;
	float sea = COLOR.r;
	float spacing = exp2(COLOR.g * 20.0 - 4.0);
	float dist = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
	vec3 disp = vec3(0.0);
	float h = 0.0;
	for (int i = 0; i < wave_count; i++) {
		float lam = wave_p[i].z;
		float fade = smoothstep(3.0 * spacing, 6.0 * spacing, lam) * (1.0 - smoothstep(lam * 25.0, lam * 60.0, dist));
		float a = wave_k[i].w * fade * mix(wave_p[i].w, 1.0, sea) * shoal(depth, lam);
		if (a < 0.0005) continue;
		vec3 K = wave_k[i].xyz;
		vec3 D = normalize(K - up * dot(K, up));
		float th = dot(K, q) - wave_p[i].x;
		float c = cos(th);
		disp += up * (a * c) - D * (wave_p[i].y * a * sin(th));
		h += a * c;
	}
	VERTEX += disp;
	v_q = q;
	v_up = up;
	v_depth = depth;
	v_sea = sea;
	v_height = h;
}

void fragment() {
	vec3 up = normalize(v_up);
	float dist = length(VERTEX);
	vec3 nh = vec3(0.0);
	float jac = 1.0;   // compression de la surface : < 0,3 sur les crêtes qui déferlent
	float ripple = 0.0;
	float hsum = 0.0;
	for (int i = 0; i < wave_count; i++) {
		float lam = wave_p[i].z;
		float fade = 1.0 - smoothstep(lam * 40.0, lam * 160.0, dist);
		float a = wave_k[i].w * fade * mix(wave_p[i].w, 1.0, v_sea) * shoal(v_depth, lam);
		if (a < 0.0002) continue;
		vec3 K = wave_k[i].xyz;
		float k = length(K);
		vec3 D = normalize(K - up * dot(K, up));
		float th = dot(K, v_q) - wave_p[i].x;
		float s = sin(th), c = cos(th);
		nh += D * (a * k * s);
		jac -= wave_p[i].y * a * k * c;
		if (lam < 4.0) ripple += a * c / max(lam, 0.05);
		hsum += a;
	}
	vec3 n = normalize(up * max(jac, 0.08) + nh);
	vec3 nv = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(n, 0.0)).xyz);

	// Couleur de l'eau selon la profondeur (le fond sableux transparaît près des plages)
	float d = max(v_depth, 0.0);
	vec3 water = mix(shallow_color, deep_color, 1.0 - exp(-d / 12.0));
	water = mix(lake_color, water, v_sea);
	water = mix(sand_color * 0.8, water, smoothstep(0.0, 2.5, d) * 0.85 + 0.15);
	// crêtes plus claires (lumière qui traverse la vague)
	water += vec3(0.0, 0.05, 0.045) * clamp(v_height / max(hsum, 0.05), 0.0, 1.0) * v_sea;

	// Écume : moutons au sommet des vagues raides, et rouleaux sur la plage
	float caps = smoothstep(0.45, 0.05, jac) * whitecaps;
	float breakup = 0.5 + 0.5 * clamp(ripple * 6.0, -1.0, 1.0);
	float shore = 1.0 - smoothstep(0.1, 2.2, v_depth);
	float bands = 0.5 + 0.5 * sin(v_depth * 4.0 - time_s * 1.3 + ripple * 3.0);
	float shoreFoam = shore * smoothstep(0.35, 0.9, bands) * step(0.0, v_depth) * v_sea + smoothstep(0.35, 0.0, v_depth) * step(-0.05, v_depth) * 0.8;
	float foam = clamp(caps * breakup + shoreFoam * breakup, 0.0, 1.0);
	foam *= 1.0 - smoothstep(2000.0, 8000.0, dist);

	ALBEDO = mix(water, vec3(0.92, 0.95, 0.96) * (1.0 - night * 0.8), foam);
	ROUGHNESS = mix(0.035 + 0.22 * smoothstep(300.0, 150000.0, dist), 0.7, foam);
	METALLIC = 0.0;
	SPECULAR = 0.5;
	NORMAL = nv;
	if (!FRONT_FACING) {
		// vue de dessous (plongée) : surface argentée et bleutée
		NORMAL = -nv;
		ALBEDO = vec3(0.03, 0.16, 0.19);
		ROUGHNESS = 0.25;
	}
}
