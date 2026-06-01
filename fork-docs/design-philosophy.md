# Design Philosophy

This fork has one practical goal: keep strict parsing useful without making the
fork painful to maintain.

That means two things:

- keep the patch set small, so rebasing onto new Kavita releases stays realistic;
- keep the feature reversible, so users can go back to upstream without losing
  data.

Everything else follows from those two constraints.

## What Should Move Upstream

This fork would be easier to maintain if the generic pieces eventually moved
into Kavita itself:

- **Per-library parser strictness.** Some libraries are organized enough that
  folder names should be trusted.
- **Folder-path fallback.** If matching by name fails, matching by
  `Series.FolderPath` can prevent parser changes from creating duplicate series.
- **Parser logs.** A simple "what did the scanner do?" page is useful even
  outside strict mode.
- **Small `{key-value}` metadata tokens.** Folder-level IDs and metadata are a
  stable, pre-scan hint.

The fork code is written with that in mind: fork-only logic lives in fork-only
files where possible, and shared upstream files are touched only where needed.

## Keep Level 0 Untouched

Level 0 is upstream Kavita behavior.

Strict parsing only runs when `Library.ParserStrictnessLevel >= 1`. A library
imported from upstream starts at level 0, and nothing changes until the user
opts in.

Why this matters:

- users can enable strict mode one library at a time;
- disabling strict mode is the first debugging step;
- upstream parser behavior stays available without fork code in the way.

## Keep the Schema Small

The fork adds one database column:

```text
Library.ParserStrictnessLevel INTEGER NOT NULL DEFAULT 0
```

There are no new permanent tables. Parser logs are kept in memory and disappear
on restart.

Why this matters:

- upstream Kavita tolerates the extra column;
- switching back does not require a migration;
- the feature does not leave much behind.

## Avoid Editing Hot Upstream Code

Every change to an upstream-owned file is a future rebase conflict. When the
fork needs new behavior, it usually adds a sibling type instead of changing an
existing one.

Examples:

| Upstream | Fork addition |
| --- | --- |
| `IReadingItemService` | `IStrictReadingItemService` |
| `LibraryController` | `ParserLogController` |
| `LibraryService` | `ParserLogService` |
| `ReadingItemService` | `StrictReadingItemService` |

This keeps most fork behavior isolated. If the feature is disabled, upstream
paths stay close to upstream.

## Preserve `SeriesId`

The most important data-safety problem is series identity.

If a scan produces a different series name for the same folder, Kavita can miss
the old row and create a new one. Reading progress, bookmarks, collections,
ratings, On Deck, and reading lists are all tied to the old `SeriesId`, so that
looks like data loss.

The fork adds a fallback:

```text
match by name
or match by Series.FolderPath
```

If the folder path matches an existing row, the scanner reuses that row and
updates its name. The `SeriesId` stays stable.

This helps when moving between level 0, level 1, and level 2. It also helps when
switching back toward upstream behavior before leaving the fork.

It is not magic. If upstream previously treated a deeper folder as the series
folder, strict mode may not be able to map it back automatically. Backups still
matter before the first strict scan.

## Reuse Existing Metadata Locks

Folder tokens can set metadata such as language, release year, and publication
status. When the fork sets those fields, it uses Kavita's existing lock flags:

- `LanguageLocked`
- `ReleaseYearLocked`
- `PublicationStatusLocked`

No fork-only lock system is added.

External IDs are different. Fields like `AniListId`, `MalId`, and `ComicVineId`
do not have lock flags upstream. The fork can set them during a scan, but later
metadata jobs may overwrite them. The docs call this out instead of pretending
those IDs are protected.

## Keep `ParserInfo` Small

`ParserInfo` is used throughout Kavita's parser pipeline, so changing it has a
high rebase cost.

The fork only adds `SeriesReleaseYear`. Other folder-scoped metadata is
re-derived by `StrictMetadataApplier` instead of being pushed through
`ParserInfo`.

This keeps the shared parser data structure close to upstream.

## Reserve `extra*` Tokens

Strict mode accepts `{extra<Anything>-<value>}` tokens and ignores them today.

Examples:

- `{extraOriginalFilename-Old Name.cbz}`
- `{extraGroup-ScanTeam}`
- `{extraQuality-1220p}`
- `{extraSource-Web}`

This lets users preserve provenance when renaming files for strict mode without
making the parser reject those names.

## Test the Contract

The strict parser has focused tests for:

- folder-as-series identity;
- metadata token parsing;
- strict vs lenient filename handling;
- parser logs;
- folder-path fallback;
- level-change data preservation.

The synthetic catalog in `Test Data/StrictParser/` also renders into real `.cbz`
files and image folders for UI checks. It uses made-up series, so screenshots
and bug reports do not leak a real library.

## Documentation Is Part of the Feature

Strict mode changes how users organize libraries, so the rules need to be
visible.

- `FORK.md` is the practical overview.
- `parser-strictness.md` is the detailed parser contract.
- `migrating-back-to-upstream.md` explains how to leave the fork safely.

The goal is not more paperwork. The goal is that future rebases, bug reports,
and possible upstream PRs do not require rediscovering why the fork is shaped
this way.
