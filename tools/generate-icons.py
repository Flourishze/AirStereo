"""AirStereo vector artwork, rasterized at each target size (Pillow, build-time only).

No downloaded artwork. The SVG and pixels share the same geometric primitives.
Run: python tools/generate-icons.py
"""
from pathlib import Path
from io import BytesIO
import math
import struct
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets" / "icons"
OUT.mkdir(parents=True, exist_ok=True)
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def artwork(size, kind):
    s = 8
    scale = size * s / 64
    image = Image.new("RGBA", (size * s, size * s))
    draw = ImageDraw.Draw(image)

    def rounded(bounds, radius, color):
        draw.rounded_rectangle(tuple(round(n * scale) for n in bounds), radius * scale, fill=color)

    def curve(start, control, end, width, color):
        points, left, right = [], [], []
        for i in range(97):
            t = i / 96
            point = tuple(((1-t)**2 * start[j] + 2*(1-t)*t*control[j] + t*t*end[j])*scale for j in (0, 1))
            dx, dy = (2*(1-t)*(control[j]-start[j])+2*t*(end[j]-control[j]) for j in (0,1))
            length = math.hypot(dx,dy)
            nx, ny = -dy/length*width*scale/2, dx/length*width*scale/2
            points.append(point)
            left.append((point[0]+nx,point[1]+ny))
            right.append((point[0]-nx,point[1]-ny))
        draw.polygon(left+right[::-1],fill=color)
        r = width*scale/2
        for x, y in (points[0], points[-1]):
            draw.ellipse((x-r, y-r, x+r, y+r), fill=color)

    if kind == "app":
        mask = Image.new("L", image.size)
        ImageDraw.Draw(mask).rounded_rectangle(tuple(round(n*scale) for n in (3,3,61,61)), 15*scale, fill=255)
        gradient = Image.new("RGBA", image.size)
        gd = ImageDraw.Draw(gradient)
        for y in range(image.height):
            t = y / max(1, image.height-1)
            c = tuple(round(a*(1-t)+b*t) for a,b in zip((29,176,247), (35,73,204)))
            gd.line((0,y,image.width,y), fill=c+(255,))
        image.paste(gradient,(0,0),mask)
        draw = ImageDraw.Draw(image)
        curve((19,24),(32,10),(45,24),3.8,"white")
        if size >= 24:
            curve((26,26),(32,20),(38,26),2.8,(201,235,255,255))
        rounded((17,31,26,49),4.5,"white")
        rounded((38,31,47,49),4.5,"white")
    else:
        color = (247,249,252,255) if kind == "tray-light" else (32,35,42,255)
        # More open geometry and thicker bars for 16/20px notification areas.
        curve((12,23),(32,3),(52,23),6,color)
        rounded((11,32,23,55),5.8,color)
        rounded((41,32,53,55),5.8,color)
    return image.resize((size,size), Image.Resampling.LANCZOS)


def write_ico(path, frames):
    data = []
    for frame in frames:
        stream = BytesIO()
        frame.save(stream, "PNG")
        data.append(stream.getvalue())
    offset = 6 + 16*len(frames)
    with path.open("wb") as f:
        f.write(struct.pack("<HHH",0,1,len(frames)))
        for frame, payload in zip(frames,data):
            size = frame.width
            f.write(struct.pack("<BBBBHHII",size%256,size%256,0,0,1,32,len(payload),offset))
            offset += len(payload)
        for payload in data:
            f.write(payload)


svg_head = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">'
(OUT / "AirStereo.svg").write_text(svg_head + '''
<defs><linearGradient id="blue" x2="0" y2="1"><stop stop-color="#1db0f7"/><stop offset="1" stop-color="#2349cc"/></linearGradient></defs>
<rect x="3" y="3" width="58" height="58" rx="15" fill="url(#blue)"/>
<path d="M19 24Q32 10 45 24" fill="none" stroke="white" stroke-width="3.8" stroke-linecap="round"/>
<path d="M26 26Q32 20 38 26" fill="none" stroke="#c9ebff" stroke-width="2.8" stroke-linecap="round"/>
<g fill="white"><rect x="17" y="31" width="9" height="18" rx="4.5"/><rect x="38" y="31" width="9" height="18" rx="4.5"/></g></svg>
''', encoding="utf-8")
for variant, color in (("light", "#f7f9fc"),("dark", "#20232a")):
    (OUT / f"tray-{variant}.svg").write_text(svg_head + f'''
<path d="M12 23Q32 3 52 23" fill="none" stroke="{color}" stroke-width="6" stroke-linecap="round"/>
<g fill="{color}"><rect x="11" y="32" width="12" height="23" rx="5.8"/><rect x="41" y="32" width="12" height="23" rx="5.8"/></g></svg>
''', encoding="utf-8")

for kind, filename in (("app","AirStereo"),("tray-light","tray-light"),("tray-dark","tray-dark")):
    frames = [artwork(size,kind) for size in SIZES]
    write_ico(OUT / (filename+".ico"),frames)
    frames[-1].save(OUT / (filename+".png"))

# A compact proof sheet at actual tray sizes, on both taskbar backgrounds.
sheet = Image.new("RGB",(880,440),(243,245,249))
d = ImageDraw.Draw(sheet)
font_path = Path("C:/Windows/Fonts/segoeui.ttf")
font = ImageFont.truetype(str(font_path),18) if font_path.exists() else ImageFont.load_default()
big = ImageFont.truetype(str(font_path),25) if font_path.exists() else font
d.text((28,20),"AirStereo / Stereo, in the air",fill="#20232a",font=big)
for x, size in ((32,160),(226,64),(326,32),(396,16)):
    tile = artwork(size,"app")
    sheet.paste(tile,(x,85),tile)
    d.text((x,260),f"{size}px",fill="#5e6270",font=font)
for y, bg, kind, caption in ((315,"#202024","tray-light","Dark taskbar"),(377,"#e5e8ee","tray-dark","Light taskbar")):
    d.rounded_rectangle((24,y,856,y+48),radius=10,fill=bg)
    d.text((40,y+12),caption,fill="#ededf3" if kind=="tray-light" else "#272933",font=font)
    for x,size in zip((310,400,490,580,670),(16,20,24,28,32)):
        mark = artwork(size,kind)
        sheet.paste(mark,(x,y+(48-size)//2),mark)
sheet.save(OUT / "icon-preview.png")
print(f"Wrote multi-size icons and SVG sources to {OUT}")
