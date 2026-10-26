"""Animazione per README e sito: il puntatore si ferma sull'icona e il pannello si apre, poi si richiude.

Composta dalle immagini vere dell'app (--snapshot): pannello e icona sono quelli renderizzati da WPF; desktop,
barra delle applicazioni e puntatore sono disegnati qui. Serve Pillow (non è una dipendenza del motore).

  py -3.12 tools/hover_gif.py <cartella istantanee> <file .gif> [--theme dark|light]
"""
import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H, BAR = 560, 720, 48
FPS = 15
FONTS = Path("C:/Windows/Fonts")


def ease(t):
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


def font(name, size):
    try:
        return ImageFont.truetype(str(FONTS / name), size)
    except OSError:
        return ImageFont.load_default()


def background(dark):
    # desktop a tinta unita: una sfumatura nella tavolozza GIF di 256 colori diventerebbe a bande
    img = Image.new("RGB", (W, H), (44, 46, 78) if dark else (214, 222, 238))
    d = ImageDraw.Draw(img)
    bar = (32, 32, 36) if dark else (238, 238, 242)
    d.rectangle([0, H - BAR, W, H], fill=bar)
    d.line([(0, H - BAR), (W, H - BAR)], fill=(60, 60, 66) if dark else (210, 210, 216))
    ink = (235, 235, 240) if dark else (30, 30, 34)
    ui = font("segoeui.ttf", 12)
    d.text((W - 16, H - BAR + 13), "16:20", font=ui, fill=ink, anchor="ra")
    d.text((W - 16, H - BAR + 29), "26/10/2026", font=ui, fill=ink, anchor="ra")
    icons = font("SegoeIcons.ttf", 16)
    for x, glyph in ((W - 112, "\ue767"), (W - 138, "\ue701"), (W - 196, "\ue70e")):   # volume, wifi, freccia
        d.text((x, H - BAR / 2), glyph, font=icons, fill=ink, anchor="mm")
    return img


def cursor():
    pts = [(0, 0), (0, 17), (4, 13), (7, 20), (10, 19), (7, 12), (12, 12)]
    c = Image.new("RGBA", (16, 24), (0, 0, 0, 0))
    ImageDraw.Draw(c).polygon(pts, fill=(255, 255, 255, 255), outline=(0, 0, 0, 255))
    return c


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("shots", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--theme", choices=["dark", "light"], default="dark")
    a = ap.parse_args()
    dark = a.theme == "dark"
    base = background(dark)
    tray = Image.open(a.shots / f"tray-63-{'dark' if dark else 'light'}.png").convert("RGBA").resize((22, 22), Image.LANCZOS)
    icon_xy = (W - 167, H - BAR // 2)
    base.paste(tray, (icon_xy[0] - 11, icon_xy[1] - 11), tray)
    panel = Image.open(a.shots / f"panel-{a.theme}.png").convert("RGBA")
    pw = 320
    panel = panel.resize((pw, round(panel.height * pw / panel.width)), Image.LANCZOS)
    px, py = W - pw - 12, H - BAR - 12 - panel.height
    # ombra morbida, come la cornice DWM di Windows 11
    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([px, py + 6, px + pw, py + panel.height + 6], 10, fill=(0, 0, 0, 120))
    shadow = shadow.filter(ImageFilter.GaussianBlur(14))
    mask = Image.new("L", panel.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, pw - 1, panel.height - 1], 8, fill=255)
    arrow = cursor()

    start, onicon, inside, away = (150, 330), (icon_xy[0] - 2, icon_xy[1] - 4), (px + 150, py + 260), (110, 300)
    # (durata s, posizione del puntatore da -> a, visibilità del pannello da -> a, scorrimento)
    steps = [(0.6, start, start, 0, 0), (0.8, start, onicon, 0, 0), (0.45, onicon, onicon, 0, 0),
             (0.3, onicon, onicon, 0, 1), (1.0, onicon, onicon, 1, 1), (0.6, onicon, inside, 1, 1),
             (1.2, inside, inside, 1, 1), (0.6, inside, away, 1, 1), (0.6, away, away, 1, 1),
             (0.3, away, away, 1, 0), (0.7, away, away, 0, 0)]
    frames, durations = [], []
    for dur, p0, p1, v0, v1 in steps:
        n = max(1, round(dur * FPS))
        for i in range(n):
            k = ease((i + 1) / n)
            pos = (round(p0[0] + (p1[0] - p0[0]) * k), round(p0[1] + (p1[1] - p0[1]) * k))
            vis = v0 + (v1 - v0) * k
            f = base.copy().convert("RGBA")
            if vis > 0:
                lift = round((1 - vis) * 14)
                sh = shadow.copy()
                sh.putalpha(shadow.getchannel("A").point(lambda x: round(x * vis)))
                f.alpha_composite(sh)
                m = mask.point(lambda x: round(x * vis))
                f.paste(panel, (px, py + lift), m)
            f.alpha_composite(arrow, pos)
            frames.append(f.convert("RGB"))
            durations.append(round(1000 / FPS))
    # una sola tavolozza per tutte le immagini: niente sfarfallio dei colori fra un fotogramma e l'altro
    # il pannello pesa più dello sfondo: i suoi testi e colori devono restare fedeli
    sample = Image.new("RGB", (W + pw * 3, H))
    sample.paste(frames[0], (0, 0))
    for i in range(3):
        sample.paste(panel.convert("RGB"), (W + pw * i, 0))
    palette = sample.quantize(colors=255, method=Image.Quantize.FASTOCTREE, kmeans=3)
    out = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    a.out.parent.mkdir(parents=True, exist_ok=True)
    out[0].save(a.out, save_all=True, append_images=out[1:], duration=durations, loop=0, optimize=True, disposal=1)
    print(f"{a.out}: {len(out)} fotogrammi, {a.out.stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
