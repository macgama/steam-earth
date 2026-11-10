#!/usr/bin/env python3
"""Pictogrammes des transports -> data/transport/icons.rgba.gz (pastilles 64×64 RGBA, à la suite).

Dessinés à partir de la police Noto Color Emoji (Google, licence SIL Open Font 1.1 : utilisable dans un jeu
vendu, voir data/transport/LICENCES.md). Format brut (pas de PNG) pour que Godot ne réimporte rien :
en-tête « taille nombre » sur une ligne, puis les pixels RGBA de chaque icône.
Ordre = enum TransitLayer.Mode.
"""
import gzip, os
from PIL import Image, ImageDraw, ImageFont

FONT = "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf"
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "data", "transport", "icons.rgba.gz")
S = 64
# (emoji, couleur de la pastille)
ICONS = [
    ("\U0001F686", (200, 30, 40)),    # Train (gare)
    ("\U0001F687", (20, 60, 160)),    # Métro
    ("\U0001F68A", (20, 130, 70)),    # Tram
    ("\U0001F68C", (230, 170, 0)),    # Bus
    ("\U0001F68F", (230, 170, 0)),    # Gare routière
    ("⛴️", (0, 120, 190)),  # Bateau (débarcadère, port)
    ("\U0001F69E", (120, 70, 160)),   # Funiculaire / train de montagne
    ("✈️", (40, 40, 60)),   # Aéroport
    ("\U0001F6A1", (120, 70, 160)),   # Téléphérique
]

def icon(ch, col):
    font = ImageFont.truetype(FONT, 109)
    g = Image.new("RGBA", (136, 128), (0, 0, 0, 0))
    ImageDraw.Draw(g).text((0, 0), ch, font=font, embedded_color=True)
    g = g.crop(g.getbbox()).resize((44, 44), Image.LANCZOS)
    big = Image.new("RGBA", (S * 4, S * 4), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    d.ellipse((4, 4, S * 4 - 5, S * 4 - 5), fill=(255, 255, 255, 255))
    d.ellipse((16, 16, S * 4 - 17, S * 4 - 17), fill=col + (255,))
    im = big.resize((S, S), Image.LANCZOS)
    inner = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # disque clair sous l'emoji pour qu'il reste lisible sur la couleur
    ImageDraw.Draw(inner).ellipse((7, 7, S - 8, S - 8), fill=(255, 255, 255, 235))
    im = Image.alpha_composite(im, inner)
    im.alpha_composite(g, ((S - 44) // 2, (S - 44) // 2))
    return im

def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    ims = [icon(c, col) for c, col in ICONS]
    with gzip.open(OUT, "wb", 9) as f:
        f.write(f"{S} {len(ims)}\n".encode())
        for im in ims:
            f.write(im.tobytes())
    sheet = Image.new("RGBA", (S * len(ims), S), (90, 120, 90, 255))
    for i, im in enumerate(ims):
        sheet.alpha_composite(im, (i * S, 0))
    sheet.save(os.environ.get("PREVIEW", "/tmp/icons_preview.png"))
    print(len(ims), "icônes ->", os.path.getsize(OUT), "octets")

if __name__ == "__main__":
    main()
