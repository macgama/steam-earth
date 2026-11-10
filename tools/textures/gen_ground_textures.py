"""Textures de sol procédurales (créées ici, donc libres de droits), raccordables (tileables), 512×512.

Chaque texture est une variation autour d'un gris moyen (≈ 0,5) colorée légèrement : le shader du terrain
la multiplie par la couleur de la couverture des sols (WorldCover), sauf la roche et la neige qui ont leur
propre couleur. Usage : python gen_ground_textures.py ../../data/textures
"""
import sys, os
import numpy as np
from PIL import Image

N = 512
rng = np.random.default_rng(7)
out = sys.argv[1]

def noise(beta, seed, n=N):
    """Bruit fractal raccordable (spectre en 1/f^beta), normalisé 0..1."""
    r = np.random.default_rng(seed)
    f = np.fft.fftfreq(n)
    fx, fy = np.meshgrid(f, f)
    k = np.sqrt(fx ** 2 + fy ** 2); k[0, 0] = 1
    spec = (r.normal(size=(n, n)) + 1j * r.normal(size=(n, n))) / k ** beta
    spec[0, 0] = 0
    a = np.real(np.fft.ifft2(spec))
    a = (a - a.min()) / (a.max() - a.min())
    return a

def blades(seed, count, length, width=1):
    """Traits courts (brins d'herbe, chaume), raccordables."""
    r = np.random.default_rng(seed)
    img = np.zeros((N, N))
    for _ in range(count):
        x, y = r.uniform(0, N, 2)
        ang = r.normal(-np.pi / 2, 0.5)
        L = r.uniform(length * 0.5, length)
        v = r.uniform(0.4, 1)
        for t in np.linspace(0, L, int(L * 2)):
            xi = int(x + np.cos(ang) * t) % N; yi = int(y + np.sin(ang) * t) % N
            img[yi, xi:xi + width] = np.maximum(img[yi, xi:xi + width], v)
    return img

def save(name, rgb):
    rgb = np.clip(rgb, 0, 1)
    Image.fromarray((rgb * 255).astype(np.uint8)).save(os.path.join(out, name + ".png"), optimize=True)
    print(name, rgb.reshape(-1, 3).mean(0).round(3))

def tint(g, c):
    return g[..., None] * np.array(c)[None, None, :]

# Herbe : brins + taches, autour de 0,5
b = blades(1, 26000, 9)
g = 0.42 + 0.22 * noise(1.2, 2) + 0.25 * b - 0.08 * noise(0.6, 3)
save("grass", tint(g, (0.95, 1.05, 0.85)))

# Sol forestier : aiguilles, feuilles, terre
g = 0.35 + 0.3 * noise(1.0, 4) + 0.2 * blades(5, 9000, 5) + 0.15 * (noise(0.3, 6) > 0.7)
save("forest", tint(g, (1.05, 1.0, 0.85)))

# Champ : sillons (lignes) + mottes
y = np.arange(N)[:, None] * np.ones((1, N))
furrow = 0.5 + 0.5 * np.sin(y / N * 2 * np.pi * 48 + 3 * noise(1.5, 7))
g = 0.35 + 0.25 * furrow + 0.2 * noise(0.8, 8) + 0.15 * blades(9, 8000, 4)
save("field", tint(g, (1.05, 1.0, 0.9)))

# Terre / sable : grains
g = 0.4 + 0.25 * noise(1.3, 10) + 0.2 * noise(0.2, 11) + 0.1 * (rng.random((N, N)) > 0.97)
save("dirt", tint(g, (1.08, 1.0, 0.88)))

# Roche : grandes masses + fissures, couleur propre (gris chaud)
n1 = noise(1.6, 12); n2 = noise(0.9, 13)
cracks = np.clip(1 - np.abs(noise(1.4, 14) - 0.5) * 30, 0, 1)
g = 0.30 + 0.35 * n1 + 0.15 * n2 - 0.18 * cracks
save("rock", tint(g, (1.0, 0.96, 0.9)))

# Neige : presque blanche, légers creux bleutés
g = 0.86 + 0.1 * noise(1.5, 15) + 0.04 * noise(0.5, 16)
snow = tint(g, (1.0, 1.0, 1.0)) - tint(0.06 * noise(1.2, 17), (0.6, 0.3, 0.0))
save("snow", snow)

# Urbain / asphalte : grain fin + taches + joints
g = 0.42 + 0.12 * noise(0.1, 18) + 0.15 * noise(1.4, 19)
x = np.arange(N)[None, :] * np.ones((N, 1))
joints = ((x % 128) < 2) | ((y % 128) < 2)
g = g - 0.12 * joints
save("urban", tint(g, (1.0, 1.0, 1.0)))
