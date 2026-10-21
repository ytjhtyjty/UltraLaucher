"""Генерирует иконку UltraLauncher (assets/ultralauncher.png и .ico).
Рисуется вручную: изометрический куб с буквой-стрелкой, без сторонних изображений."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

out = Path(sys.argv[1] if len(sys.argv) > 1 else "assets")
out.mkdir(parents=True, exist_ok=True)
S = 1024  # рисуем крупно, затем уменьшаем

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Скруглённая плитка-фон.
d.rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=(20, 24, 31, 255))
# Мягкое свечение снизу.
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([180, 560, S - 180, S - 120], fill=(61, 220, 132, 90))
glow = glow.filter(ImageFilter.GaussianBlur(70))
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)

cx, top, w, h = S // 2, 200, 300, 175
# Грани куба.
top_face = [(cx, top), (cx + w, top + h), (cx, top + 2 * h), (cx - w, top + h)]
left_face = [(cx - w, top + h), (cx, top + 2 * h), (cx, top + 2 * h + 340), (cx - w, top + h + 340)]
right_face = [(cx, top + 2 * h), (cx + w, top + h), (cx + w, top + h + 340), (cx, top + 2 * h + 340)]
d.polygon(top_face, fill=(110, 240, 160, 255))
d.polygon(left_face, fill=(61, 200, 120, 255))
d.polygon(right_face, fill=(40, 150, 92, 255))

# Буква U (Ultra) на левой и правой гранях — светлая обводка граней.
edge = (230, 255, 240, 255)
d.line(top_face + [top_face[0]], fill=edge, width=10, joint="curve")
d.line([(cx, top + 2 * h), (cx, top + 2 * h + 340)], fill=edge, width=10)
d.line([left_face[0], left_face[3], left_face[2], right_face[3], right_face[2], right_face[1]], fill=edge, width=10, joint="curve")

# Стрелка «запуск» вверх на верхней грани.
ax, ay = cx, top + h
d.polygon([(ax, ay - 105), (ax + 95, ay - 10), (ax + 35, ay - 10), (ax + 35, ay + 85),
           (ax - 35, ay + 85), (ax - 35, ay - 10), (ax - 95, ay - 10)], fill=(20, 24, 31, 235))

png = out / "ultralauncher.png"
img.resize((512, 512), Image.LANCZOS).save(png)
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save(out / "ultralauncher.ico", sizes=sizes)
print("ok", png, out / "ultralauncher.ico")
