"""Banner di presentazione (README, sito, anteprima social): icona, nome, promessa e pannelli veri dell'app.

I pannelli sono le immagini renderizzate dall'app (--snapshot, in docs/images); sfondo, testi e decorazioni sono
disegnati qui. Serve Pillow (non è una dipendenza del motore).

  py -3.12 tools/banner.py docs/images/banner.png
"""
import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
IMAGES = ROOT / "docs" / "images"
FONTS = Path("C:/Windows/Fonts")
S = 2                                   # disegnato a 2x: 2560x1280, nitido sugli schermi ad alta densità
W, H = 1280 * S, 640 * S
INK, MUTED, ACCENT, CURVE = (243, 241, 248), (187, 184, 207), (255, 195, 90), (199, 166, 242)


def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size * S)


def lerp(a, b, k):
    return tuple(round(x + (y - x) * k) for x, y in zip(a, b))


def background():
    img = Image.new("RGB", (W, H))
    top_left, bottom_right = (16, 17, 38), (52, 36, 98)
    px = img.load()
    for y in range(0, H, 2):
        for x in range(0, W, 2):
            c = lerp(top_left, bottom_right, min(1.0, (x / W) * 0.55 + (y / H) * 0.45))
            px[x, y] = px[x + 1, y] = px[x, y + 1] = px[x + 1, y + 1] = c
    # bagliore caldo dietro i pannelli: il "crepuscolo" della batteria
    glow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(glow).ellipse([W * 0.55, H * 0.05, W * 1.05, H * 1.15], fill=110)
    glow = glow.filter(ImageFilter.GaussianBlur(160 * S))
    img = Image.composite(Image.new("RGB", (W, H), (255, 170, 80)), img, glow.point(lambda v: v // 2))
    return img.convert("RGBA")


def forecast_curve(img):
    """Una curva di scarica stilizzata con la sua fascia, come nel grafico dell'app, molto tenue sullo sfondo."""
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    pts, upper, lower = [], [], []
    for i in range(0, 101):
        k = i / 100
        x = round(-40 * S + k * (W * 0.62))
        y = round(H * 0.30 + (H * 0.58) * (k ** 1.35))
        pts.append((x, y))
        spread = 18 * S + 70 * S * k
        upper.append((x, y - spread))
        lower.append((x, y + spread))
    d.polygon(upper + lower[::-1], fill=CURVE + (14,))
    d.line(pts, fill=CURVE + (50,), width=3 * S, joint="curve")
    img.alpha_composite(layer)


def rounded(panel, radius):
    mask = Image.new("L", panel.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, panel.width - 1, panel.height - 1], radius, fill=255)
    out = panel.copy()
    out.putalpha(mask)
    return out


def place(img, panel, xy, radius=22 * S, shadow=60):
    x, y = xy
    sh = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([x, y + 24 * S, x + panel.width, y + panel.height + 24 * S], radius,
                                         fill=(0, 0, 0, shadow * 2))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(36 * S)))
    img.alpha_composite(rounded(panel, radius), (x, y))


def pill(img, x, y, text, f):
    # su un livello a parte: ImageDraw non fonde la trasparenza, sostituirebbe i pixel
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    w = d.textlength(text, font=f)
    h = 46 * S
    d.rounded_rectangle([x, y, x + w + 40 * S, y + h], h // 2, fill=(255, 255, 255, 16), outline=ACCENT + (170,), width=2 * S)
    img.alpha_composite(layer)
    ImageDraw.Draw(img).text((x + 20 * S, y + h / 2), text, font=f, fill=INK, anchor="lm")
    return x + w + 40 * S + 14 * S


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out", type=Path)
    ap.add_argument("--small", type=Path, help="anche una copia 1280x640 (anteprima social)")
    a = ap.parse_args()

    img = background()
    forecast_curve(img)

    # pannelli veri: in carica dietro, a batteria davanti (escono dal bordo in basso)
    charging = Image.open(IMAGES / "charging-dark.png").convert("RGBA")
    charging = charging.resize((280 * S, round(charging.height * 300 * S / charging.width)), Image.LANCZOS)
    charging = Image.blend(Image.new("RGBA", charging.size, (20, 20, 40, 255)), charging, 0.82)
    place(img, charging, (W - 580 * S, 170 * S), shadow=45)
    panel = Image.open(IMAGES / "panel-dark.png").convert("RGBA")
    panel = panel.resize((340 * S, round(panel.height * 340 * S / panel.width)), Image.LANCZOS)
    place(img, panel, (W - 400 * S, 70 * S))

    d = ImageDraw.Draw(img)
    x0 = 84 * S
    icon = Image.open(IMAGES / "icon-256.png").convert("RGBA").resize((92 * S, 92 * S), Image.LANCZOS)
    img.alpha_composite(icon, (x0, 92 * S))
    d.text((x0 + 112 * S, 92 * S + 46 * S), "Battery Forecast", font=font("seguisb.ttf", 58), fill=INK, anchor="lm")

    head = font("segoeuib.ttf", 44)
    d.text((x0, 238 * S), "How long will your laptop", font=head, fill=INK)
    d.text((x0, 294 * S), "really last today?", font=head, fill=ACCENT)
    d.text((x0, 378 * S), "An AI battery forecast that learns how you use", font=font("segoeui.ttf", 23), fill=MUTED)
    d.text((x0, 410 * S), "your PC, on battery and while charging.", font=font("segoeui.ttf", 23), fill=MUTED)

    pf = font("seguisb.ttf", 18)
    x = x0
    for t in ("Local AI · Timestral", "Works offline", "Free"):
        x = pill(img, x, 470 * S, t, pf)
    d = ImageDraw.Draw(img)
    d.text((x0, 560 * S), "Windows 10 & 11  ·  x64 & ARM64  ·  a Windows fork of Till Dusk",
           font=font("segoeui.ttf", 17), fill=(150, 146, 178))

    a.out.parent.mkdir(parents=True, exist_ok=True)
    img.convert("RGB").save(a.out, optimize=True)
    print(f"{a.out}: {img.width}x{img.height}, {a.out.stat().st_size / 1024:.0f} KB")
    if a.small:
        img.convert("RGB").resize((W // S, H // S), Image.LANCZOS).save(a.small, optimize=True)
        print(f"{a.small}: {W // S}x{H // S}, {a.small.stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
