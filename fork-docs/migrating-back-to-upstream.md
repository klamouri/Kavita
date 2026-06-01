# Migrating Back to Upstream Kavita

If you never enabled strict mode, switching back is simple: use the upstream
image with the same config volume.

If any library used strict mode, do one cleanup scan while still running the
fork first. That gives the fork a chance to preserve `SeriesId` while moving
series names back toward upstream parser output.

Optional cleanup steps are at the bottom if you want a pristine database.

## What the fork actually changes on disk

| Change | Detail | Survives switching back to upstream? |
| --- | --- | --- |
| One new column on `Library` | `ParserStrictnessLevel INTEGER NOT NULL DEFAULT 0` | Yes. EF Core uses `SELECT explicit-columns FROM Library`, so an unknown extra column is silently tolerated by the upstream binary. No error, no data loss. |
| `SeriesMetadata.LanguageLocked` / `PublicationStatusLocked` / `ReleaseYearLocked` flags set to `true` for fork-managed series | Upstream-owned columns; only their meaning is fork-driven | Yes. Upstream respects these locks the same way (its own ProcessSeries checks them). |
| `Series.AniListId`, `MalId`, `HardcoverId`, `MetronId`, `ComicVineId`, `MangaBakaId` populated from folder tokens | Upstream-owned columns | Yes. They're just data. Kavita+ scrobbler / `ExternalMetadataService` will overwrite them on the next match cycle if you run those services. |
| New tables | **None.** The Parser Logs page is backed by an in-memory ring buffer that dies with the process. | N/A |
| New files in `/kavita/config/` | None | N/A |

## Before swapping the image: re-normalize series names while the fork is still running

> ⚠️ **Do this step first if any of your libraries was ever on strictness
> level 1 or 2.** Skip it and the first upstream scan will rebucket strict-named
> series under upstream-style names, orphaning their reading progress / On
> Deck / bookmarks / collections / reading-list links.

The fork has a folder-path fallback in its series matcher that quietly migrates
series from one parser's naming to the other's *while preserving the SeriesId*.
That fallback only exists in the fork — once you switch to the upstream
binary, every series the strict parser had renamed will look like a "new"
folder to the upstream matcher.

The fix is to use the fork itself to pre-normalize names to upstream-shape
before swapping:

1. **Still running the fork**, edit each library that's on level 1 or 2 and
   set its parser strictness back to **Default (level 0)**.
2. From the library card's three-dot menu, click **Settings → Force Scan**.
   The folder-path fallback finds each existing series row by its
   `FolderPath`, updates `Name` / `NormalizedName` / `SortName` to whatever
   the upstream regex parser produces, and leaves the SeriesId stable. All
   your reading progress, On Deck, bookmarks, etc. stay attached.
3. Verify in the UI: series should now show their upstream-style names. If
   anything looks wrong, this is the moment to restore from your DB backup
   — you're still on the fork.
4. *Then* proceed to swap the image (steps below).

After step 4, the upstream binary will match series by the now-upstream-shape
names. No rebucketing, no orphans.

> Skipping step 1–3 doesn't necessarily lose data — upstream may parse some
> files into the same name the strict parser used, in which case those
> series merge cleanly. The risk is per-series and depends on each folder's
> filename pattern. Doing the level-0 force-scan first eliminates the
> guesswork.

## Default migration: swap the image, keep the volume

```yaml
# docker-compose.yml — swap the image, keep the volume
services:
  kavita:
    image: jvmilazz0/kavita:latest    # was: ghcr.io/<owner>/kavita-strict-fork:<tag>
    container_name: kavita
    volumes:
      - ./config:/kavita/config       # unchanged
      - ./manga:/manga                # unchanged
    ports:
      - 5000:5000
```

```bash
docker compose pull
docker compose up -d --force-recreate
```

(Use `--force-recreate` so the container is recreated from the new image.)

On first start, upstream Kavita reads `kavita.db`. It doesn't see the
`ParserStrictnessLevel` column in its EF model, so it ignores it. Every
library reverts to the upstream parser cascade. Your series, reading
progress, collections, reading lists, bookmarks, and metadata locks are
all intact.

## What you'll observe

- **The "Parser strictness (fork)" radio group** in the library settings
  modal is gone. The setting is still in the DB, just not editable from
  upstream's UI.
- **The Parser Logs page** is gone (it was a fork-only UI route).
- **Fork-set metadata stays put.** A series whose language was set to `ja`
  via a `{language-ja}` folder token still shows `ja`, with the lock icon
  next to it. Upstream's "refresh metadata" won't overwrite it. To free
  the field, click the lock icon in the series-edit modal (per-field).
- **External IDs (AniListId etc.)** stay as data but lose their "set from
  folder token" provenance. If you run Kavita+, the scrobbler may overwrite
  them on its next match cycle.
- **Folder names with `{key-value}` tokens** stay on disk as-is. Upstream's
  parser doesn't recognize the token grammar, so series names may regress
  to whatever upstream's regex cascade extracts (e.g. a folder named
  `Crimson Veil (2020) {aniListId-90001}` may end up parsed as series name
  `Crimson Veil (2020) {aniListId-90001}` literally, or split unpredictably
  depending on filenames inside).

If folder-name regressions are unacceptable, **rename folders back to a
shape upstream parses cleanly** before switching. If you renamed files for
strict mode, `FORK.md` explains the `{extraOriginalFilename-...}` convention for
keeping the old filename visible on disk.

## Optional cleanup: pristine schema

If you want zero fork remnants in the schema, run this once after switching
to upstream:

```bash
# Backup first.
cp config/kavita.db config/kavita.db.fork-backup

# Drop the fork column.
sqlite3 config/kavita.db "ALTER TABLE Library DROP COLUMN ParserStrictnessLevel;"
```

SQLite 3.35+ supports `DROP COLUMN` natively. On older SQLite, do the
rebuild dance:

```sql
BEGIN;
CREATE TABLE Library_new AS SELECT
    Id, Name, CoverImage, /* ... every other column ... */
FROM Library;
DROP TABLE Library;
ALTER TABLE Library_new RENAME TO Library;
-- Re-create any indexes that referenced the original table.
COMMIT;
```

Confirm the upstream binary still boots after the schema change before
deleting the backup.

## Optional cleanup: unlock fork-set metadata

If you want upstream to re-derive metadata (year, language, publication
status) from ComicInfo or Kavita+ instead of holding the fork's values:

```bash
sqlite3 config/kavita.db <<'SQL'
UPDATE SeriesMetadata SET ReleaseYearLocked = 0 WHERE ReleaseYearLocked = 1;
UPDATE SeriesMetadata SET LanguageLocked = 0 WHERE LanguageLocked = 1;
UPDATE SeriesMetadata SET PublicationStatusLocked = 0 WHERE PublicationStatusLocked = 1;
SQL
```

This bulk-clears every lock the fork set. Note: it also clears locks the
user set manually in the upstream UI — there's no way to distinguish.
Skip this step unless you specifically want all fields re-derivable.

## Going back from upstream to the fork

Symmetric: swap the image back, do nothing else. The `ParserStrictnessLevel`
column is still there (if you didn't drop it). Existing libraries are at
level 0 (no behavior change). Raise the level per-library via the UI when
ready.

## TL;DR

| Scenario | Steps |
| --- | --- |
| "Just put me back on upstream, don't touch anything" | Change image, restart. Done. |
| "Pristine schema, but accept the metadata as-is" | + drop the `ParserStrictnessLevel` column |
| "Pristine schema + let upstream re-derive metadata" | + drop the column + clear the three Locked columns |
