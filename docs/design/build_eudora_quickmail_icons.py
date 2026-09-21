from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).parent / "EudoraQuickMail-v2"
MASTER_DIR = ROOT / "masters"
PNG_DIR = ROOT / "png"
SIZES = (16, 24, 32, 48, 64, 128, 256)
# Small Windows icon slots benefit from optical sizing.  The paper-plane mark is
# naturally wide and the normal 84% master padding makes it look smaller than
# neighbouring taskbar icons.  Fill the small canvases, then progressively
# restore the regular breathing room for larger artwork.
OPTICAL_EXTENTS = {
    16: 1024,
    24: 1024,
    32: 1024,
    48: 1024,
    64: 960,
}


def normalize_master(source: Path) -> Image.Image:
    image = Image.open(source).convert("RGBA")
    alpha = image.getchannel("A")
    mask = alpha.point(lambda value: 255 if value >= 16 else 0)
    bounds = mask.getbbox()
    if bounds is None:
        raise ValueError(f"No visible pixels in {source}")

    cropped = image.crop(bounds)
    target_extent = 860
    scale = min(target_extent / cropped.width, target_extent / cropped.height)
    resized = cropped.resize(
        (round(cropped.width * scale), round(cropped.height * scale)),
        Image.Resampling.LANCZOS,
    )

    canvas = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    position = ((1024 - resized.width) // 2, (1024 - resized.height) // 2)
    canvas.alpha_composite(resized, position)
    return canvas


def render_icon(master: Image.Image, size: int) -> Image.Image:
    """Render one optically sized icon frame without changing the master."""
    if size not in OPTICAL_EXTENTS:
        return master.resize((size, size), Image.Resampling.LANCZOS)

    alpha = master.getchannel("A")
    mask = alpha.point(lambda value: 255 if value >= 16 else 0)
    bounds = mask.getbbox()
    if bounds is None:
        raise ValueError("Master contains no visible pixels")

    cropped = master.crop(bounds)
    target_extent = OPTICAL_EXTENTS[size] * size / 1024
    scale = min(target_extent / cropped.width, target_extent / cropped.height)
    rendered = cropped.resize(
        (
            max(1, round(cropped.width * scale)),
            max(1, round(cropped.height * scale)),
        ),
        Image.Resampling.LANCZOS,
    )
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    position = ((size - rendered.width) // 2, (size - rendered.height) // 2)
    canvas.alpha_composite(rendered, position)
    return canvas


def save_icon_family(stem: str) -> dict[int, Image.Image]:
    master = normalize_master(MASTER_DIR / f"{stem}.png")
    PNG_DIR.mkdir(parents=True, exist_ok=True)
    master.save(PNG_DIR / f"{stem}-1024.png", optimize=True)

    rendered: dict[int, Image.Image] = {}
    for size in SIZES:
        rendered[size] = render_icon(master, size)
        rendered[size].save(PNG_DIR / f"{stem}-{size}.png", optimize=True)

    rendered[max(SIZES)].save(
        ROOT / f"{stem}.ico",
        format="ICO",
        sizes=[(size, size) for size in SIZES if size <= 256],
        append_images=[rendered[size] for size in SIZES[:-1]],
    )
    return rendered


def create_preview(families: dict[str, dict[int, Image.Image]]) -> None:
    tile = 170
    label_height = 28
    width = tile * len(SIZES)
    height = label_height + tile * len(families) * 2 + 300
    sheet = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default()

    for column, size in enumerate(SIZES):
        x = column * tile
        draw.text((x + 8, 9), f"{size} px", fill="#0b1f4d", font=font)

    row = 0
    for name, images in families.items():
        for background in ("#f7f8fb", "#111827"):
            y = label_height + row * tile
            for column, size in enumerate(SIZES):
                x = column * tile
                draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=background)
                display_size = min(size, 128)
                icon = images[size].resize(
                    (display_size, display_size), Image.Resampling.LANCZOS
                )
                sheet.paste(
                    icon,
                    (
                        x + (tile - display_size) // 2,
                        y + (tile - display_size) // 2,
                    ),
                    icon,
                )
            draw.text((8, y + 8), name, fill="#111827" if background == "#f7f8fb" else "white", font=font)
            row += 1

    zoom_y = label_height + row * tile + 20
    draw.text((8, zoom_y), "Small-size pixel preview (8x)", fill="#0b1f4d", font=font)
    x = 8
    for name, images in families.items():
        for size in (16, 24, 32):
            zoomed = images[size].resize((size * 8, size * 8), Image.Resampling.NEAREST)
            sheet.paste(zoomed, (x, zoom_y + 24), zoomed)
            draw.text((x, zoom_y + 28 + size * 8), f"{name} {size}", fill="#0b1f4d", font=font)
            x += size * 8 + 18

    sheet.save(ROOT / "preview-contact-sheet.png", optimize=True)


families = {
    "EudoraQuickMail": save_icon_family("EudoraQuickMail"),
    "EudoraQuickMailReceived": save_icon_family("EudoraQuickMailReceived"),
}
create_preview(families)

for stem in families:
    ico = Image.open(ROOT / f"{stem}.ico")
    expected_ico_sizes = {(size, size) for size in SIZES if size <= 256}
    actual_ico_sizes = ico.info.get("sizes", set())
    if actual_ico_sizes != expected_ico_sizes:
        raise ValueError(f"Unexpected ICO sizes for {stem}: {actual_ico_sizes}")
    for size in (*SIZES, 1024):
        png = Image.open(PNG_DIR / f"{stem}-{size}.png")
        if png.size != (size, size) or png.mode != "RGBA":
            raise ValueError(f"Invalid PNG export: {stem}-{size}.png")
        if png.getchannel("A").getextrema() != (0, 255):
            raise ValueError(f"PNG does not contain full transparency: {stem}-{size}.png")
    print(f"Validated {stem}: PNG {SIZES + (1024,)}; ICO {sorted(actual_ico_sizes)}")

archive_path = ROOT / "EudoraQuickMail-icon-set-v2.zip"
with ZipFile(archive_path, "w", ZIP_DEFLATED) as archive:
    for path in sorted(ROOT.rglob("*")):
        if path.is_file() and path != archive_path:
            archive.write(path, path.relative_to(ROOT))
    archive.write(Path(__file__), Path(__file__).name)
print(f"Created {archive_path}")
