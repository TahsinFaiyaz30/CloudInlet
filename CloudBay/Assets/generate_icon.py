"""Rebuild the CloudBay Windows icon from CloudBay.svg.

Requires ``python -m pip install pillow cairosvg``. The app itself does not
depend on these design-time packages.
"""

from io import BytesIO
from pathlib import Path

import cairosvg
from PIL import Image


HERE = Path(__file__).resolve().parent
SIZES = (16, 24, 32, 48, 64, 128, 256)


def main() -> None:
    svg = HERE / "CloudBay.svg"
    large_png = cairosvg.svg2png(
        url=str(svg), output_width=1024, output_height=1024
    )
    with Image.open(BytesIO(large_png)) as rendered:
        source = rendered.convert("RGBA")
        for size in SIZES:
            small = source.resize((size, size), Image.Resampling.LANCZOS)
            small.save(HERE / f"CloudBay-{size}.png", optimize=True)
        source.resize((256, 256), Image.Resampling.LANCZOS).save(
            HERE / "CloudBay.ico",
            format="ICO",
            sizes=[(size, size) for size in SIZES],
        )


if __name__ == "__main__":
    main()
