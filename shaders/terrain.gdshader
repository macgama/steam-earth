// Terrain : couleur par sommet (comme l'ancien StandardMaterial3D) + lumières des villes la nuit.
// Les lumières (NASA Black Marble, ~5 km) sont lues par pixel dans une texture mondiale lat/lon,
// et ne s'allument que du côté nuit de la planète (calculé par pixel à partir du Soleil).
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D city_lights : filter_linear_mipmap, repeat_enable;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // vers le Soleil, repère monde
uniform vec3 planet_center = vec3(0.0, -6371000.0, 0.0);
uniform mat3 planet_basis_inv = mat3(1.0);    // monde → repère planète (Y = axe des pôles)
uniform float lights_strength = 1.0;           // atténué près du sol (Planet.cs)
uniform vec3 lights_color : source_color = vec3(1.0, 0.76, 0.42);

varying vec3 world_pos;

vec3 srgb_to_linear(vec3 c) {
	return mix(pow((c + 0.055) / 1.055, vec3(2.4)), c / 12.92, step(c, vec3(0.04045)));
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	ALBEDO = srgb_to_linear(COLOR.rgb);
	ROUGHNESS = 0.95;
	vec3 up = normalize(world_pos - planet_center);
	float night = smoothstep(0.03, -0.10, dot(up, sun_dir)); // s'allume après le coucher du soleil
	if (night > 0.0) {
		vec3 p = planet_basis_inv * up;                       // x = cos(lat)cos(lon), y = sin(lat), z = -cos(lat)sin(lon)
		float lat = asin(clamp(p.y, -1.0, 1.0));
		float lon = atan(-p.z, p.x);
		vec2 uv = vec2(lon / (2.0 * PI) + 0.5, 0.5 - lat / PI);
		float c = texture(city_lights, uv).r;
		float l = c * 3.0 + c * c * 6.0;                      // centres-villes plus vifs que la banlieue
		EMISSION = lights_color * l * night * lights_strength;
	}
}
