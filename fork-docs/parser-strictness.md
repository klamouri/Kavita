# Parser Strictness

This fork adds a per-library parser strictness setting:

- **Default (level 0):** upstream Kavita parsing. The fork parser does not run.
- **Strict (level 1):** each top-level folder is one series, with lenient filename handling.
- **Stricter (level 2):** same folder rule, but filenames must use clearer kind tokens.

Level 0 exists so the fork can stay close to upstream. If a library is left at
level 0, it should behave like regular Kavita.

## Before You Enable It

Changing strictness changes how Kavita identifies series on the next scan.
Back up `config/kavita.db` first.

Then enable it in the library settings:

```text
Library settings -> Advanced -> Parser strictness (fork)
```

You can also set it with SQL:

```sql
UPDATE Library SET ParserStrictnessLevel = 1 WHERE Id = <id>;
-- 0 = Default, 1 = Strict, 2 = Stricter
```

## Folder Rule

At levels 1 and 2, every immediate child folder of the library root is treated
as one series.

```text
Library root/
├── Series Alpha (2020) {aniListId-50007}/
│   ├── Series Alpha - V01.cbz
│   ├── Series Alpha - V02.cbz
│   └── Specials/
│       └── Series Alpha - SP1 - Color Edition.cbz
└── Series Beta {malId-2}/
    └── Series Beta - C50.cbz
```

The series folder is parsed from right to left:

1. `{key-value}` tokens are removed and kept as metadata.
2. A trailing `(YYYY)` becomes the series release year.
3. Whatever remains is the series name.

Nested folders do not create nested series. They are still part of the same
top-level series folder.

## Filename Rule

Files should include one kind token:

| Token | Meaning |
| --- | --- |
| `V1`, `T1`, `Volume 1`, `Tome 1` | Volume |
| `C1`, `CH1`, `Chapter 1` | Chapter |
| `SP1` | Special |

Numbers may use decimals, such as `C12.5`.

Level 2 requires the kind token to be the first word of a `" - "`-separated
segment:

```text
Series Alpha - V01 - Title.cbz
Series Alpha - C12.5 - Title.cbz
Series Alpha - SP1 - Color Edition.cbz
```

Level 1 is more forgiving:

- it can find a kind token elsewhere in the filename;
- a file under `Specials/` becomes a special even without `SP1`;
- a file with no kind token is kept as loose leaf instead of being rejected.

The Parser Logs page marks these level-1 fallbacks as `Lenient`, because level
2 would reject them.

## Metadata Tokens

Token format:

```text
{key-value}
```

The key must start with a letter and then use only letters or numbers. The
value can contain hyphens, but not `{` or `}`. Keys are case-insensitive.

These tokens set external IDs:

| Token | Field |
| --- | --- |
| `{aniListId-50007}` | `Series.AniListId` |
| `{malId-2}` | `Series.MalId` |
| `{hardcoverId-123}` | `Series.HardcoverId` |
| `{metronId-123}` | `Series.MetronId` |
| `{comicVineId-cv-50}` | `Series.ComicVineId` |
| `{comicVineSeriesId-4050-123}` | `Series.ComicVineId` |
| `{mangaBakaId-123}` | `Series.MangaBakaId` |

External ID tokens can be placed on the series folder or on files. Folder
tokens are applied while parsing each file in that folder. If the same field is
also present on a file, the file token wins for that parsed file.

When Kavita writes the series row, it uses the first valid parsed value it sees
for each external ID. `comicVineSeriesId` takes priority over `comicVineId`.

These folder tokens set and lock series metadata:

| Token | Field |
| --- | --- |
| `(2020)` at the end of the folder name | `SeriesMetadata.ReleaseYear` |
| `{language-en}` | `SeriesMetadata.Language` |
| `{publicationStatus-Completed}` | `SeriesMetadata.PublicationStatus` |

`publicationStatus` accepts Kavita's enum names, such as `OnGoing`, `Hiatus`,
`Completed`, `Cancelled`, and `Ended`.

Invalid numbers and invalid publication statuses are ignored. Unknown tokens
are also ignored.

## `extra*` Tokens

Any token whose key starts with `extra` is accepted and ignored today:

```text
{extraOriginalFilename-Old Name.cbz}
{extraGroup-ScanTeam}
{extraQuality-1220p}
{extraSource-Web}
```

Use these when you need to rename files for strict mode but still want to keep
the old name or other provenance in the filename. They are only a safe naming
convention in this release; they are not stored as queryable metadata.

## What Gets Locked

Strict mode locks the metadata fields it sets on `SeriesMetadata`:

- release year;
- language;
- publication status.

External IDs are different. Upstream Kavita does not have lock flags for
`Series.AniListId`, `Series.MalId`, `Series.ComicVineId`, and similar fields.
The fork sets them during a scan, but later metadata jobs can still overwrite
them.

## Parser Logs

Admin users can inspect strict parsing here:

```text
Settings -> Info -> Parser Logs
```

The page records files parsed by level 1 or level 2 libraries. It does not
record level 0 libraries.

Each row shows:

- `Accepted`: valid even under level 2;
- `Lenient`: accepted by level 1, but level 2 would reject it;
- `Rejected`: not imported by the strict parser.

The log is in memory, keeps about the 2000 newest entries, and resets when the
server restarts. It can also be cleared from the page.

## Known Limits

- Loose files directly under the library root are rejected. Put files inside a
  series folder.
- Level 2 does not infer specials from the `Specials/` folder. Use an `SP<n>`
  token.
- Mixed archive and loose-image content in the same series folder may still
  split into separate Kavita series rows, because upstream keys series by name
  and format.
- Strict mode is not a nested-folder system. The top-level series folder is the
  identity.

That last point is intentional. The goal is a predictable manga/webtoon library
where the filesystem matches the flat series list Kavita shows in the UI.
