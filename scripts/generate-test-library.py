#!/usr/bin/env python3
"""
Renderer for the synthetic Kavita strict-parser test library.

Reads the catalog declaration at
``Kavita.Services.Tests/Test Data/StrictParser/catalog.json`` and renders the
cbz / loose-png tree under
``Kavita.Services.Tests/Test Data/StrictParser/generated/all-cases/manga/``.

The catalog is the **single source of truth**. To add or change a test case,
hand-edit ``catalog.json`` directly. This script does not synthesize the
catalog from any in-code declaration; it only renders what's already there.

Used by both:
  * the MSBuild ``GenerateTestLibrary`` target in ``Kavita.Services.Tests``
    (invokes this script after bootstrapping a venv with Pillow)
  * direct invocation by a developer: ``python3 generate-test-library.py``

Output bytes are deterministic given the same catalog.json + Pillow version +
system font picked.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import shutil
import sys
import time
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

SCRIPT_DIR = Path(__file__).resolve().parent              # <repo>/scripts/
REPO_ROOT = SCRIPT_DIR.parent
FIXTURES = REPO_ROOT / "Kavita.Services.Tests" / "Test Data" / "StrictParser"
CATALOG_PATH = FIXTURES / "catalog.json"
DEFAULT_OUTPUT = FIXTURES / "generated" / "all-cases" / "manga"

IMAGE_W, IMAGE_H = 1200, 1800
FIXED_MTIME_TUPLE = (1980, 1, 1, 0, 0, 0)
FIXED_MTIME_EPOCH = time.mktime((1980, 1, 1, 0, 0, 0, 0, 0, -1))
ALL_CASES_TIER_LABEL = "ALL CASES"

FONT_CANDIDATES = [
    "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
    "/System/Library/Fonts/Supplemental/Arial.ttf",
    "/System/Library/Fonts/Helvetica.ttc",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
]


# --- font / color helpers ------------------------------------------------------

_FONT_CACHE: dict[int, ImageFont.FreeTypeFont] = {}
_PICKED_FONT_PATH: str | None = None


def pick_font_path() -> str | None:
    global _PICKED_FONT_PATH
    if _PICKED_FONT_PATH is not None:
        return _PICKED_FONT_PATH
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            try:
                ImageFont.truetype(path, 24)
                _PICKED_FONT_PATH = path
                return path
            except OSError:
                continue
    return None


def get_font(size: int):
    if size in _FONT_CACHE:
        return _FONT_CACHE[size]
    path = pick_font_path()
    font = ImageFont.truetype(path, size) if path else ImageFont.load_default()
    _FONT_CACHE[size] = font
    return font


def lighten(rgb, amount: float = 0.55) -> tuple:
    return tuple(int(c + (255 - c) * amount) for c in rgb)


def text_color_for(bg) -> tuple:
    luminance = 0.2126 * bg[0] + 0.7152 * bg[1] + 0.0722 * bg[2]
    return (250, 250, 250) if luminance < 140 else (30, 30, 30)


# --- text drawing --------------------------------------------------------------

def draw_centered_text(draw, xy, text, font, fill, max_width=None):
    lines = [text] if max_width is None else wrap_text(text, font, max_width)
    line_h = font.size + int(font.size * 0.15)
    total_h = line_h * len(lines)
    x, y = xy
    start_y = y - total_h // 2 + line_h // 2
    for i, line in enumerate(lines):
        draw.text((x, start_y + i * line_h), line, font=font, fill=fill, anchor="mm")


def wrap_text(text, font, max_width):
    words = text.split()
    if not words:
        return [""]
    lines = []
    cur = []
    for word in words:
        attempt = " ".join(cur + [word])
        bbox = font.getbbox(attempt)
        w = bbox[2] - bbox[0]
        if w <= max_width or not cur:
            cur.append(word)
        else:
            lines.append(" ".join(cur))
            cur = [word]
    if cur:
        lines.append(" ".join(cur))
    return lines


# --- page rendering ------------------------------------------------------------

def kind_label(kind: str) -> str:
    return {"V": "VOLUME", "T": "VOLUME", "C": "CHAPTER", "CH": "CHAPTER", "SP": "SPECIAL"}.get(kind, "VOLUME")


def kind_short(kind: str) -> str:
    return {"V": "V", "T": "V", "C": "C", "CH": "C", "SP": "SP"}.get(kind, "V")


def make_cover(series: dict, file_entry: dict) -> Image.Image:
    w, h = IMAGE_W, IMAGE_H
    bg = tuple(series["cover_color"])
    img = Image.new("RGB", (w, h), bg)
    draw = ImageDraw.Draw(img)
    fg = text_color_for(bg)
    cover = file_entry["cover"]
    circle_color = series.get("circle_color")

    if circle_color:
        cc = tuple(circle_color)
        cx, cy = w // 2, int(h * 0.46)
        r = int(w * 0.34)
        draw.ellipse([(cx - r, cy - r), (cx + r, cy + r)], fill=cc)
        circle_fg = text_color_for(cc)
        draw.text((cx, cy), cover["num"], font=get_font(360), fill=circle_fg, anchor="mm")

    draw_centered_text(draw, (w // 2, int(h * 0.13)), series["name"], get_font(140), fg, max_width=int(w * 0.9))

    if not circle_color:
        marker = f"{kind_label(cover['kind'])} {cover['num']}"
        draw_centered_text(draw, (w // 2, int(h * 0.52)), marker, get_font(180), fg)

    if cover.get("subtitle"):
        sub_y = 0.79 if circle_color else 0.66
        draw_centered_text(draw, (w // 2, int(h * sub_y)), cover["subtitle"], get_font(78), fg, max_width=int(w * 0.85))

    meta_big = get_font(64)
    meta_small = get_font(56)
    year = cover.get("year") or series["year"]
    bottom_lines = [
        (series["author"], meta_big),
        (series["publisher"], meta_small),
        (f"{year}    {ALL_CASES_TIER_LABEL}", meta_small),
    ]
    y = int(h * 0.84)
    for text, font in bottom_lines:
        draw_centered_text(draw, (w // 2, y), text, font, fg)
        y += int(font.size * 1.4)

    return img


def make_inner_page(series: dict, file_entry: dict, page_idx: int) -> Image.Image:
    w, h = IMAGE_W, IMAGE_H
    bg = lighten(tuple(series["cover_color"]), 0.72)
    img = Image.new("RGB", (w, h), bg)
    draw = ImageDraw.Draw(img)
    accent = (90, 90, 90)
    fg = (40, 40, 40)
    cover = file_entry["cover"]
    n = file_entry["page_count"]

    meta = f"{series['name']}   {kind_short(cover['kind'])}{cover['num']}   {ALL_CASES_TIER_LABEL}"
    draw_centered_text(draw, (w // 2, 70), meta, get_font(46), accent, max_width=int(w * 0.95))

    draw_centered_text(draw, (w // 2, int(h * 0.46)), f"PAGE {page_idx}", get_font(280), fg)
    draw_centered_text(draw, (w // 2, int(h * 0.64)), f"of {n}", get_font(120), accent)

    if cover.get("subtitle"):
        draw_centered_text(draw, (w // 2, int(h * 0.86)), cover["subtitle"], get_font(56), accent, max_width=int(w * 0.9))

    return img


def render_pages_for_entry(series: dict, file_entry: dict) -> list[Image.Image]:
    n = file_entry["page_count"]
    pages = [make_cover(series, file_entry)]
    for i in range(2, n + 1):
        pages.append(make_inner_page(series, file_entry, i))
    return pages


# --- archive writing -----------------------------------------------------------

def write_cbz(path: Path, pages: list[Image.Image]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        path.unlink()
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for i, img in enumerate(pages, start=1):
            buf = io.BytesIO()
            img.save(buf, format="PNG", optimize=False, compress_level=9)
            info = zipfile.ZipInfo(filename=f"page-{i:03d}.png", date_time=FIXED_MTIME_TUPLE)
            info.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(info, buf.getvalue())
    os.utime(path, (FIXED_MTIME_EPOCH, FIXED_MTIME_EPOCH))


def write_loose_images(folder: Path, pages: list[Image.Image]) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    for i, img in enumerate(pages, start=1):
        out = folder / f"{i:02d}.png"
        img.save(out, format="PNG", optimize=False, compress_level=9)
        os.utime(out, (FIXED_MTIME_EPOCH, FIXED_MTIME_EPOCH))


# --- top-level orchestration ---------------------------------------------------

def render_catalog(catalog: dict, out_root: Path) -> dict:
    if out_root.parent.exists():
        shutil.rmtree(out_root.parent)
    out_root.mkdir(parents=True, exist_ok=True)

    stats = {"series_folders": 0, "cbz_files": 0, "png_files": 0}
    series_seen: set[str] = set()
    for series in catalog["series"]:
        for entry in series["files"]:
            pages = render_pages_for_entry(series, entry)
            full = out_root / entry["relative_path"]
            if entry["format"] == "cbz":
                write_cbz(full, pages)
                stats["cbz_files"] += 1
            elif entry["format"] == "loose-images-folder":
                write_loose_images(full, pages)
                stats["png_files"] += len(pages)
            else:
                raise ValueError(f"Unknown format: {entry['format']}")
            series_seen.add(entry["relative_path"].split("/", 1)[0])
    stats["series_folders"] = len(series_seen)
    return stats


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output-dir", type=Path, default=DEFAULT_OUTPUT,
        help=f"Where to write generated files (default: {DEFAULT_OUTPUT}).",
    )
    args = parser.parse_args()

    if not CATALOG_PATH.exists():
        sys.stderr.write(f"catalog.json not found at {CATALOG_PATH}\n")
        return 1

    font_path = pick_font_path()
    if font_path is None:
        sys.stderr.write("WARN: no TrueType font found; covers will use Pillow's tiny default.\n")
    else:
        print(f"Using font: {font_path}")

    catalog = json.loads(CATALOG_PATH.read_text())
    print(f"Rendering {len(catalog['series'])} series → {args.output_dir}")
    stats = render_catalog(catalog, args.output_dir)
    print(f"  {stats['series_folders']} series folders, {stats['cbz_files']} cbz files, {stats['png_files']} loose pngs")
    return 0


if __name__ == "__main__":
    sys.exit(main())
