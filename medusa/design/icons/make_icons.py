#!/usr/bin/env python3
"""Generates the Medusa icon options: SVG sources, VectorDrawables, PNG previews, the comparison sheet and
(optionally) the legacy launcher PNGs.

    pip install cairosvg pillow
    python3 make_icons.py            # design files + previews + res/ vector drawables
    python3 make_icons.py --legacy 2 # also write legacy mipmap PNGs and the README icon for option 2

Switching the app icon: in app/src/main/res/mipmap-anydpi-v26/ic_launcher.xml (and ic_launcher_round.xml) change the
three references from opt1 to optN, then run with --legacy N for the old-Android PNGs.
All art is on a 108 x 108 adaptive-icon canvas; the safe zone is the central 66 x 66.
"""
import math, os, sys, io
import cairosvg
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.normpath(os.path.join(HERE, "..", "..", "app", "src", "main", "res"))
C = 54.0


def circle(cx, cy, r):
    return f"M{cx - r:g},{cy:g} a{r:g},{r:g} 0 1,0 {2 * r:g},0 a{r:g},{r:g} 0 1,0 {-2 * r:g},0 Z"


def ellipse(cx, cy, rx, ry):
    return f"M{cx - rx:g},{cy:g} a{rx:g},{ry:g} 0 1,0 {2 * rx:g},0 a{rx:g},{ry:g} 0 1,0 {-2 * rx:g},0 Z"


def poly(pts):
    return "M" + " L".join(f"{x:g},{y:g}" for x, y in pts) + " Z"


def shape(d, fill=None, stroke=None, sw=0, cap="butt", evenodd=False, mono=True):
    return dict(d=d, fill=fill, stroke=stroke, sw=sw, cap=cap, evenodd=evenodd, mono=mono)


# ---------- option 1: a play triangle half stone, half live ----------
def option1():
    stone = [(40, 34), (58, 43.5), (55, 50), (60, 56), (57, 60), (58, 64.5), (40, 74)]
    live = [(59.5, 43.8), (78, 54), (59.5, 64.8), (58.5, 60.2), (61.5, 56), (56.5, 50.2)]
    return dict(
        name="Stone play", bg="#18222E",
        shapes=[
            shape(poly(stone), fill="#9AA3AC"),
            shape(poly([(40, 34), (49, 38.8), (45, 52), (40, 74)]), fill="#7D868F", mono=False),
            shape("M44,60 L50,64 L47,69", stroke="#68717A", sw=1.6, cap="round", mono=False),
            shape("M47,43 L52,47", stroke="#68717A", sw=1.6, cap="round", mono=False),
            shape(poly(live), fill="#2DD4BF"),
            shape(poly([(59.5, 43.8), (78, 54), (70, 54)]), fill="#7CF0DF", mono=False),
        ])


# ---------- option 2: a gorgon head, snakes for hair, a play button in her face ----------
def option2():
    snakes = ["M46,50 C33,47 30,36 39,31", "M51,46 C44,38 46,29 53,26", "M57,46 C64,38 62,29 55,26",
              "M62,50 C75,47 78,36 69,31", "M40,58 C28,58 24,49 28,42", "M68,58 C80,58 84,49 80,42"]
    # note: 53,26 / 55,26 meet near the top: keep them distinct
    snakes[1] = "M50,47 C42,40 43,30 49,26"
    snakes[2] = "M58,47 C66,40 65,30 59,26"
    heads = [(39, 31), (49, 26), (59, 26), (69, 31), (28, 42), (80, 42)]
    sh = [shape(d, stroke="#5BD16B", sw=4.2, cap="round") for d in snakes]
    sh += [shape(circle(x, y, 2.6), fill="#5BD16B") for x, y in heads]
    sh += [shape(circle(x + 0.3, y - 0.3, 0.9), fill="#241A33", mono=False) for x, y in heads]
    face = circle(54, 62, 18) + " " + poly([(48.5, 53.5), (48.5, 70.5), (63, 62)])
    sh.append(shape(face, fill="#C3C9D0", evenodd=True))
    return dict(name="Gorgon", bg="#2A1F3D", shapes=sh)


# ---------- option 3: a snake coiled into a film reel ----------
def option3():
    r = 24.0
    a0, a1 = math.radians(-20), math.radians(270)
    x0, y0 = C + r * math.cos(a0), C + r * math.sin(a0)
    x1, y1 = C + r * math.cos(a1), C + r * math.sin(a1)
    body = f"M{x0:.2f},{y0:.2f} A{r:g},{r:g} 0 1 1 {x1:.2f},{y1:.2f}"
    sh = [shape(body, stroke="#2E7D4F", sw=8, cap="round"),
          shape(body, stroke="#4FAE74", sw=2.2, cap="butt", mono=False)]
    for ang in (90, 210, 330):
        sh.append(shape(circle(C + 11 * math.cos(math.radians(ang)), C + 11 * math.sin(math.radians(ang)), 5.6), fill="#7D858D"))
    sh.append(shape(circle(C, C, 3.2), fill="#7D858D"))
    hx, hy = x1 + 6, y1
    sh.append(shape(ellipse(hx, hy, 9, 6), fill="#2E7D4F"))
    sh.append(shape(circle(hx + 1.5, hy - 2, 1.7), fill="#F4E9D0", mono=False))
    sh.append(shape(circle(hx + 1.9, hy - 2, 0.8), fill="#1A1A1A", mono=False))
    sh.append(shape(f"M{hx + 9:g},{hy:g} L{hx + 14:g},{hy:g} M{hx + 14:g},{hy:g} L{hx + 17:g},{hy - 2.5:g} M{hx + 14:g},{hy:g} L{hx + 17:g},{hy + 2.5:g}",
                    stroke="#D9423E", sw=1.5, cap="round"))
    return dict(name="Reel snake", bg="#EFE6D2", shapes=sh)


OPTIONS = {1: option1(), 2: option2(), 3: option3()}


# ---------- writers ----------
def svg_shapes(shapes, mono=False):
    out = []
    for s in shapes:
        if mono and not s["mono"]:
            continue
        fill = ("#000" if s["fill"] else "none") if mono else (s["fill"] or "none")
        a = f'<path d="{s["d"]}" fill="{fill}"'
        if s["stroke"]:
            a += f' stroke="{"#000" if mono else s["stroke"]}" stroke-width="{s["sw"]:g}" stroke-linecap="{s["cap"]}" stroke-linejoin="round"'
        if s["evenodd"]:
            a += ' fill-rule="evenodd"'
        out.append(a + "/>")
    return "\n  ".join(out)


def svg(opt, with_bg=True, mono=False, grid=False):
    body = svg_shapes(opt["shapes"], mono)
    bg = f'<rect width="108" height="108" fill="{opt["bg"]}"/>\n  ' if with_bg else ""
    g = '<circle cx="54" cy="54" r="33" fill="none" stroke="#f0f" stroke-width=".4"/>' if grid else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108" width="432" height="432">\n  {bg}{body}\n  {g}\n</svg>\n'


def vector(opt, mono=False):
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             '<vector xmlns:android="http://schemas.android.com/apk/res/android"',
             '    android:width="108dp" android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">']
    for s in opt["shapes"]:
        if mono and not s["mono"]:
            continue
        a = [f'    <path android:pathData="{s["d"]}"']
        col = "#FF000000"
        if s["fill"]:
            a.append(f'        android:fillColor="{col if mono else "#FF" + s["fill"][1:]}"')
        if s["evenodd"]:
            a.append('        android:fillType="evenOdd"')
        if s["stroke"]:
            a.append(f'        android:strokeColor="{col if mono else "#FF" + s["stroke"][1:]}" android:strokeWidth="{s["sw"]:g}"')
            a.append(f'        android:strokeLineCap="{s["cap"]}" android:strokeLineJoin="round"')
        lines.append("\n".join(a) + " />")
    lines.append("</vector>")
    return "\n".join(lines) + "\n"


def render(svg_text, px):
    return Image.open(io.BytesIO(cairosvg.svg2png(bytestring=svg_text.encode(), output_width=px, output_height=px))).convert("RGBA")


def mask(px, shape):
    m = Image.new("L", (px * 4, px * 4), 0)
    d = ImageDraw.Draw(m)
    n = px * 4
    if shape == "circle":
        d.ellipse((0, 0, n - 1, n - 1), fill=255)
    else:  # squircle: superellipse |x|^4 + |y|^4 = 1
        pts = []
        for i in range(360):
            t = math.radians(i)
            c, s = math.cos(t), math.sin(t)
            pts.append(((n / 2) * (1 + math.copysign(abs(c) ** 0.5, c)), (n / 2) * (1 + math.copysign(abs(s) ** 0.5, s))))
        d.polygon(pts, fill=255)
    return m.resize((px, px), Image.LANCZOS)


def masked(img, shape):
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.paste(img, (0, 0), mask(img.size[0], shape))
    return out


def font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def main():
    legacy = int(sys.argv[sys.argv.index("--legacy") + 1]) if "--legacy" in sys.argv else None
    os.makedirs(os.path.join(HERE, "previews"), exist_ok=True)
    for n, opt in OPTIONS.items():
        d = os.path.join(HERE, f"option{n}")
        os.makedirs(d, exist_ok=True)
        open(os.path.join(d, "icon.svg"), "w").write(svg(opt))
        open(os.path.join(d, "foreground.svg"), "w").write(svg(opt, with_bg=False))
        open(os.path.join(d, "monochrome.svg"), "w").write(svg(opt, with_bg=False, mono=True))
        full = render(svg(opt), 512)
        for shp in ("circle", "squircle"):
            masked(full, shp).save(os.path.join(HERE, "previews", f"option{n}-{shp}-512.png"))
            masked(render(svg(opt), 48), shp).save(os.path.join(HERE, "previews", f"option{n}-{shp}-48.png"))
        # res: vector drawables for every option, so switching is a reference change
        os.makedirs(os.path.join(RES, "drawable"), exist_ok=True)
        open(os.path.join(RES, "drawable", f"ic_opt{n}_foreground.xml"), "w").write(vector(opt))
        open(os.path.join(RES, "drawable", f"ic_opt{n}_monochrome.xml"), "w").write(vector(opt, mono=True))
    os.makedirs(os.path.join(RES, "values"), exist_ok=True)
    open(os.path.join(RES, "values", "ic_launcher_colors.xml"), "w").write(
        '<?xml version="1.0" encoding="utf-8"?>\n<resources>\n' +
        "".join(f'    <color name="ic_opt{n}_background">{o["bg"]}</color>\n' for n, o in OPTIONS.items()) + "</resources>\n")

    # comparison sheet
    W, H = 3 * 560 + 40, 1180
    sheet = Image.new("RGB", (W, H), (250, 250, 247))
    dr = ImageDraw.Draw(sheet)
    dr.text((20, 14), "Medusa icon options: circle and squircle masks, 48 px, and the themed (monochrome) layer", fill=(40, 40, 40), font=font(26))
    for n, opt in OPTIONS.items():
        x0 = 20 + (n - 1) * 560
        full = render(svg(opt), 512)
        dr.text((x0, 60), f"Option {n}: {opt['name']}", fill=(20, 20, 20), font=font(30))
        sheet.paste(masked(full, "circle").resize((256, 256), Image.LANCZOS), (x0, 110), masked(full, "circle").resize((256, 256), Image.LANCZOS))
        sheet.paste(masked(full, "squircle").resize((256, 256), Image.LANCZOS), (x0 + 276, 110), masked(full, "squircle").resize((256, 256), Image.LANCZOS))
        # actual 48 dp at 3x density = 144 px, and 48 px
        for i, shp in enumerate(("circle", "squircle")):
            big = masked(render(svg(opt), 144), shp)
            sheet.paste(big, (x0 + i * 276, 400), big)
            small = masked(render(svg(opt), 48), shp)
            sheet.paste(small, (x0 + i * 276 + 170, 448), small)
        dr.text((x0, 560), "48 dp: 144 px (xxhdpi) and 48 px", fill=(110, 110, 110), font=font(18))
        # themed icon: the monochrome layer tinted on a pastel disc, light and dark
        mono = render(svg(opt, with_bg=False, mono=True), 432)
        for i, (bgc, fg) in enumerate(((  (203, 224, 255), (22, 46, 92)), ((46, 56, 78), (205, 224, 255)))):
            disc = Image.new("RGBA", (216, 216), (0, 0, 0, 0))
            ImageDraw.Draw(disc).ellipse((0, 0, 215, 215), fill=bgc + (255,))
            m = mono.resize((216, 216), Image.LANCZOS)
            tint = Image.new("RGBA", (216, 216), fg + (255,))
            disc.paste(tint, (0, 0), m.split()[3])
            sheet.paste(disc, (x0 + i * 276, 600), disc)
        dr.text((x0, 828), "Themed icon (monochrome layer), light and dark", fill=(110, 110, 110), font=font(18))
        # on a home-screen-like row
        row = Image.new("RGB", (540, 150), (60, 90, 120))
        sheet.paste(row, (x0, 880))
        for i, shp in enumerate(("circle", "squircle", "circle", "squircle")):
            ic = masked(render(svg(opt), 96), shp)
            sheet.paste(ic, (x0 + 24 + i * 128, 906), ic)
        dr.text((x0, 1050), opt_blurb(n), fill=(40, 40, 40), font=font(20))
    sheet.save(os.path.join(HERE, "comparison.png"))

    if legacy:
        opt = OPTIONS[legacy]
        sizes = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
        for dens, px in sizes.items():
            d = os.path.join(RES, f"mipmap-{dens}")
            os.makedirs(d, exist_ok=True)
            img = render(svg(opt), px)
            # legacy icons: a rounded square and a circle, art slightly smaller than the full canvas
            masked(img, "squircle").save(os.path.join(d, "ic_launcher.png"))
            masked(img, "circle").save(os.path.join(d, "ic_launcher_round.png"))
        masked(render(svg(opt), 512), "squircle").save(os.path.join(HERE, "medusa-512.png"))
        os.makedirs(os.path.join(HERE, "..", "..", "docs"), exist_ok=True)
        masked(render(svg(opt), 512), "squircle").save(os.path.join(HERE, "..", "..", "docs", "icon.png"))


def opt_blurb(n):
    return {1: "A play triangle, half stone, half alive.",
            2: "A gorgon head, snake hair, play button face.",
            3: "A snake coiled into a film reel."}[n]


if __name__ == "__main__":
    main()
