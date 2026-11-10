// Sol du globe : couleur « macro » par sommet (couverture des sols ESA WorldCover, pente, neige)
// + textures de détail de près (herbe, sous-bois, champ, ville, terre, roche, neige).
// Coordonnées de texture : position dans le repère planète (projection sur les 3 axes, « triplanaire »),
// recalée par chunk (CUSTOM1 = centre du chunk modulo 1 024 m) pour rester continue d'un chunk à l'autre.
shader_type spatial;
render_mode cull_back, diffuse_burley;

uniform sampler2DArray layers : filter_linear_mipmap_anisotropic, repeat_enable; // valeurs sRGB brutes (rapports autour de 1)
// 0 herbe, 1 sous-bois, 2 champ, 3 ville, 4 terre, 5 roche, 6 neige
uniform float detail_strength = 1.0;     // 0 = textures désactivées (qualité basse)
uniform float detail_distance = 1500.0;  // au-delà, seule la couleur macro reste
uniform float scale_near = 3.0;          // m par répétition (motif fin)
uniform float scale_far = 23.0;          // m par répétition (motif large, casse la répétition)

// Neige réelle : carte mondiale GFS 360×181 (R = épaisseur m, G = altitude du relief du modèle m, B = température 2 m K).
// Recalée sur le vrai relief : plus d'épaisseur en altitude, fonte quand la température locale dépasse ~1 °C.
uniform sampler2D snow_map : filter_linear, repeat_enable;
uniform float snow_enabled = 0.0;
uniform float snow_altitude_scale = 600.0; // m : l'épaisseur est multipliée par e à chaque 600 m au-dessus du relief du modèle

varying vec3 tex_pos;
varying vec3 tex_nrm;
varying vec4 w4;
varying vec2 rock_snow;
varying vec3 geo; // latitude, longitude, altitude

void vertex() {
	tex_pos = VERTEX + CUSTOM1.xyz;
	tex_nrm = NORMAL;
	w4 = CUSTOM0;
	rock_snow = UV;
	geo = CUSTOM2.xyz;
}

float weather_snow() {
	if (snow_enabled < 0.5) return 0.0;
	vec2 uv = vec2((mod(geo.y, 360.0) + 0.5) / 360.0, (90.0 - geo.x + 0.5) / 181.0);
	vec3 sm = texture(snow_map, uv).rgb;
	float dh = geo.z - sm.g;
	float t_local = sm.b - 0.0065 * dh;              // gradient de température standard
	float depth = sm.r * exp(clamp(dh / snow_altitude_scale, -6.0, 3.0));
	float cover = smoothstep(0.005, 0.06, depth) * (1.0 - smoothstep(274.0, 277.5, t_local));
	return cover;
}

vec3 srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c));
}

vec3 tri(float layer, float scale, vec3 bw) {
	vec3 p = tex_pos / scale;
	return texture(layers, vec3(p.yz, layer)).rgb * bw.x
		+ texture(layers, vec3(p.xz, layer)).rgb * bw.y
		+ texture(layers, vec3(p.xy, layer)).rgb * bw.z;
}

vec3 layer_color(float layer, vec3 bw) {
	// deux échelles mélangées : moins de répétition visible
	return tri(layer, scale_near, bw) * 0.6 + tri(layer, scale_far, bw) * 0.4;
}

void fragment() {
	vec3 macro = srgb_to_linear(COLOR.rgb);
	// neige de la météo : pas sur l'eau, peu sur les pentes raides (elle glisse)
	float wsnow = rock_snow.x < 0.0 ? 0.0 : weather_snow() * (1.0 - 0.75 * smoothstep(0.3, 0.9, rock_snow.x));
	macro = mix(macro, vec3(0.80, 0.83, 0.88), wsnow);
	float fade = detail_strength * (1.0 - smoothstep(detail_distance * 0.4, detail_distance, length(VERTEX)));
	if (fade <= 0.001 || rock_snow.x < 0.0) {
		ALBEDO = macro;
	} else {
		vec3 bw = pow(abs(normalize(tex_nrm)), vec3(4.0));
		bw /= (bw.x + bw.y + bw.z);
		float dirt = clamp(1.0 - (w4.r + w4.g + w4.b + w4.a), 0.0, 1.0);
		// détail : variation autour de 1, appliquée à la couleur macro (garde la teinte WorldCover)
		vec3 d = vec3(0.0);
		if (w4.r > 0.01) d += layer_color(0.0, bw) * w4.r / 0.57;
		if (w4.g > 0.01) d += layer_color(1.0, bw) * w4.g / 0.52;
		if (w4.b > 0.01) d += layer_color(2.0, bw) * w4.b / 0.58;
		if (w4.a > 0.01) d += layer_color(3.0, bw) * w4.a / 0.54;
		if (dirt > 0.01) d += layer_color(4.0, bw) * dirt / 0.63;
		vec3 ground = macro * d;
		// roche et neige : couleur propre de la texture
		float rock = clamp(rock_snow.x, 0.0, 1.0);
		float snow = max(clamp(rock_snow.y, 0.0, 1.0), wsnow);
		if (rock > 0.01) ground = mix(ground, srgb_to_linear(layer_color(5.0, bw)) * 0.9, rock);
		if (snow > 0.01) ground = mix(ground, srgb_to_linear(layer_color(6.0, bw)), snow);
		ALBEDO = mix(macro, ground, fade);
	}
	ROUGHNESS = 0.95;
}
