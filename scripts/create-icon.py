"""Draw the project's own geometric app icon. Development only; requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / "src" / "TransMessage.App" / "Assets"
target.mkdir(parents=True, exist_ok=True)
image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((8, 8, 248, 248), radius=57, fill="#0066CC")
draw.rounded_rectangle((52, 62, 204, 176), radius=30, fill="white")
draw.polygon([(73, 167), (72, 207), (116, 174)], fill="white")
draw.line([(88, 119), (115, 145), (169, 96)], fill="#0066CC", width=15, joint="curve")
image.save(target / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
