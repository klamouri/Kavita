# Strict parser test fixture

This folder holds the synthetic library used by the strict parser tests.

The source of truth is [`catalog.json`](catalog.json). It declares 23 made-up
series and the files that should exist under each one. The renderer in
[`scripts/generate-test-library.py`](../../../scripts/generate-test-library.py)
turns that catalog into real `.cbz` files and loose-image folders with generated
covers and fake pages.

That gives us a small, shareable library for:

- unit tests;
- local scanner testing;
- screenshots;
- reproducible UI bug reports.

No real library data is used.

## Files

| Path | Purpose |
| --- | --- |
| `catalog.json` | Hand-edited fixture definition. |
| `generated/` | Rendered library output. Gitignored. |
| `README.md` | This file. |

## Regenerate the Library

From the repo root:

```bash
dotnet build Kavita.Services.Tests/Kavita.Services.Tests.csproj -t:GenerateTestLibrary
```

The target creates a Python venv under `scripts/.venv`, installs Pillow, and
writes the rendered library to:

```text
Kavita.Services.Tests/Test Data/StrictParser/generated/all-cases/manga/
```

You can also run the renderer directly:

```bash
cd scripts
python3 -m venv .venv
.venv/bin/pip install -r requirements-test-library.txt
.venv/bin/python generate-test-library.py
```

## Edit the Catalog

To add a case, edit `catalog.json` directly, then regenerate the library.

Each series has:

- `slug`: stable test identifier;
- `name`: expected series name;
- `cover_color` / `circle_color`: generated cover styling;
- optional metadata fields such as `anilist_id`, `mal_id`, `year`, `author`,
  and `publisher`;
- `files`: relative paths and file formats to render.

Each file entry has:

- `relative_path`;
- `format`: `cbz` or `loose-images-folder`;
- `page_count`;
- `cover`: enough data to draw the generated cover.

There is no catalog dump command. The JSON is the editable format.

## Iron Garden Covers

The three Iron Garden entries are deliberately similar. They share the same
base cover color, but each edition gets a different circle color.

This makes it easy to glance at the UI and confirm strict mode kept the three
editions separate instead of merging them into one series like level 0 can.

## Series Reference

See `catalog.json` for the full declaration. Current fixture series:

| # | Series | Main point |
| --- | --- | --- |
| 1 | Crimson Veil | Named V00 prequel and folder metadata |
| 2 | Stellar Drift | Volumes plus `Specials/` |
| 3 | Iron Garden (Tankobon) | Edition separation |
| 4 | Iron Garden (Kanzenban) | Edition separation |
| 5 | Iron Garden (Color) | Edition separation |
| 6 | Wandering Lantern | Volumes plus loose chapters |
| 7 | Moonlit Tavern | Volume folder layout |
| 8 | Silent Compass | Loose images inside a volume folder |
| 9 | Quill Lantern | Loose images at series root |
| 10 | Tide Cartography | Mixed archive and image formats |
| 11 | Hollow Sparrow | Plain baseline |
| 12 | Velvet Reckoning | Scene-style names |
| 13 | Quiet Engine | Full token coverage |
| 14 | Bramble King | Mixed strictness in one series |
| 15 | Frostline Saga | Decimal volume numbers |
| 16 | Paper Lions | `T` tome tokens |
| 17 | Glasswing Court | Missing year |
| 18 | Echo Mariner | Unknown tokens |
| 19 | Driftwood Heir | Single volume |
| 20 | Salt and Cinder | Specials only |
| 21 | Brass Lantern | Chapters only |
| 22 | Cobalt Wager | Deeper subfolder rejection |
| 23 | Naked Tokens | Folder that peels to no name |
