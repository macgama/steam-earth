// Touffes d'herbe (MultiMesh) : couleur par sommet (pied sombre → pointe claire) × teinte par instance,
// balancées par le vent réel (GFS). Le haut de la touffe bouge, le pied reste fixe.
shader_type spatial;
render_mode cull_disabled, diffuse_burley;

uniform float wind_strength = 0.3;   // 0 = calme, 1 = tempête (vent à 10 m / 15 m/s)
uniform vec2 wind_dir = vec2(1.0, 0.0); // direction dans le repère local de la touffe (approximation)
uniform float fade_distance = 80.0;  // les touffes rétrécissent puis disparaissent au-delà
uniform float snow_cover = 0.0;      // neige réelle au sol (0 à 1) : l'herbe s'enfonce dessous, les pointes blanchissent
varying float tip;

void vertex() {
	float h = VERTEX.y;
	tip = h;
	VERTEX.y *= 1.0 - 0.8 * snow_cover;
	vec3 o = MODEL_MATRIX[3].xyz;
	float phase = dot(o, vec3(0.37, 0.23, 0.41)) * 0.5;
	float gust = sin(TIME * (1.6 + wind_strength * 2.0) + phase) * 0.6 + sin(TIME * 3.7 + phase * 1.7) * 0.25;
	float bend = h * h * (0.05 + wind_strength * 0.35) * (0.6 + gust);
	VERTEX.xz += wind_dir * bend;
	VERTEX.y -= bend * bend * 0.5;
	// disparition progressive au loin (pas de ligne nette)
	float d = length((MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz);
	VERTEX *= 1.0 - smoothstep(fade_distance * 0.7, fade_distance, d);
}

void fragment() {
	ALBEDO = mix(COLOR.rgb, vec3(0.85, 0.87, 0.9), snow_cover * smoothstep(0.3, 0.9, tip));
	ROUGHNESS = 0.9;
	BACKLIGHT = vec3(0.15, 0.2, 0.05);
}
