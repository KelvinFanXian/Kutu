"""Prepare the approved Kutu box mark for the app and Windows icon sizes."""

from pathlib import Path
from PIL import Image, ImageOps

assets = Path(__file__).resolve().parent.parent / "Assets"
source_path = assets / "Kutu-logo-source.png"
output_path = assets / "Kutu-logo.png"
source = Image.open(source_path).convert("RGBA")
visible = source.getchannel("A").point(lambda alpha: 255 if alpha > 64 else 0)
bounds = visible.getbbox()
if bounds is None:
    raise ValueError("Kutu logo is empty")

fitted = ImageOps.contain(source.crop(bounds), (472, 472), Image.Resampling.LANCZOS)
canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
canvas.alpha_composite(fitted, ((512 - fitted.width) // 2, (512 - fitted.height) // 2))
canvas.save(output_path, optimize=True)
canvas.resize((256, 256), Image.Resampling.LANCZOS).save(
    assets / "Kutu.ico",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
