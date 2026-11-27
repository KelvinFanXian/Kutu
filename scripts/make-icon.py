"""Package the supplied Kutu logo as a Windows multi-size icon."""

from pathlib import Path
from PIL import Image, ImageOps

assets = Path(__file__).resolve().parent.parent / "Assets"
source = Image.open(assets / "Kutu-logo.png").convert("RGBA")
canvas = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
fitted = ImageOps.contain(source, (248, 248), method=Image.Resampling.LANCZOS)
canvas.alpha_composite(fitted, ((256 - fitted.width) // 2, (256 - fitted.height) // 2))

canvas.save(
    assets / "Kutu.ico",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
