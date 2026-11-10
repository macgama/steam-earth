// Arbres (MultiMesh) : couleur par instance (essence, saison, variation), balancement par le vent réel.
// Modèles de hauteur 1 (mis à l'échelle par instance) : le bas reste fixe, la cime bouge le plus.
shader_type spatial;
render_mode cull_back, diffuse_burley;

global uniform vec4 wind;              // x = intensité 0..1, y/z = direction (est, nord), w = rafales 0..1
uniform bool use_instance_color = true; // feuillage : teinte de l'instance ; tronc : couleur fixe
uniform vec3 trunk_color : source_color = vec3(0.30, 0.22, 0.15);
uniform float stiffness = 1.0;          // palmiers et peupliers plus souples (< 1)

void vertex() {
	float h = clamp(VERTEX.y, 0.0, 1.2);
	vec3 o = MODEL_MATRIX[3].xyz;
	float phase = dot(o, vec3(0.11, 0.07, 0.13));
	float strength = wind.x * (0.6 + 0.4 * sin(TIME * 0.7 + phase * 0.3)) + wind.w * 0.3;
	float sway = sin(TIME * (0.9 + wind.x) + phase) * 0.5 + sin(TIME * 2.3 + phase * 1.9) * 0.2;
	// direction du vent dans le repère local de l'arbre : approximation (rotation aléatoire autour de la verticale)
	vec2 d = normalize(vec2(wind.y, wind.z) + vec2(0.0001));
	float bend = h * h * (0.008 + strength * 0.045) / stiffness;
	VERTEX.xz += d * bend * (1.0 + sway);
	// feuillage qui frémit un peu
	VERTEX.xz += vec2(sin(TIME * 5.0 + VERTEX.y * 9.0 + phase), cos(TIME * 4.3 + VERTEX.x * 7.0 + phase)) * 0.004 * h * (0.3 + strength);
}

void fragment() {
	vec3 c = use_instance_color ? COLOR.rgb : trunk_color;
	ALBEDO = c;
	ROUGHNESS = 0.92;
	if (use_instance_color) BACKLIGHT = c * 0.25;
}
