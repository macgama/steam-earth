// Terrain : couleur par sommet (comme l'ancien StandardMaterial3D) + lumières des villes la nuit.
// UV2.x = intensité NASA Black Marble (0..1). Le côté nuit est calculé par sommet à partir du Soleil,
// donc vu de l'espace seule la partie de la Terre dans l'ombre s'allume.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // vers le Soleil, repère monde
uniform vec3 planet_center = vec3(0.0, -6371000.0, 0.0);
uniform float lights_strength = 1.0;           // 0 = éteint (réglé selon l'altitude par Planet.cs)
uniform vec3 lights_color : source_color = vec3(1.0, 0.78, 0.45);

varying vec3 world_pos;
varying float city;

vec3 srgb_to_linear(vec3 c) {
	return mix(pow((c + 0.055) / 1.055, vec3(2.4)), c / 12.92, step(c, vec3(0.04045)));
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	city = UV2.x;
}

void fragment() {
	ALBEDO = srgb_to_linear(COLOR.rgb);
	ROUGHNESS = 0.95;
	float s = dot(normalize(world_pos - planet_center), sun_dir);
	float night = smoothstep(0.03, -0.10, s);          // s'allume après le coucher du soleil
	float l = city * city * 1.6 + city * 0.15;          // contraste : centres-villes plus vifs que la banlieue
	EMISSION = lights_color * l * night * lights_strength;
}
